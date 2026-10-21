using System.Security.Claims;
using System.Text.Json;
using ChogGames.Api.Realtime;
using ChogGames.Api.Rooms;
using ChogGames.Core.Matches;
using ChogGames.Core.Rooms;
using ChogGames.Infrastructure.Matches;
using ChogGames.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace ChogGames.Api.Matches;

/// <summary>
/// Match commands (STORY-024, ENG-003, ENG-009, DEC-026): a player sends an intent in an envelope with an
/// <c>actionId</c>, the <c>matchId</c> and the <c>expectedVersion</c> they acted on. A retry returns the first result,
/// and a command built on an older version is refused. The player is always the session's; accepted commands are
/// announced to the room's members over SignalR (STORY-022). Only the current player acts, before their turn runs out;
/// turns that run out are timed out by <see cref="TurnTimeoutWorker"/> (STORY-025, DEC-027). Shares the room endpoints'
/// rate limit.
/// </summary>
public static class MatchCommandEndpoints
{
    private static readonly JsonElement EmptyPayload = JsonSerializer.SerializeToElement(new { });

    /// <param name="Payload">A JSON object; omitted or null means no details.</param>
    public sealed record MatchCommandRequest(Guid? ActionId, Guid? MatchId, long? ExpectedVersion, string? Type, JsonElement? Payload);

    /// <param name="Version">The room version this action produced.</param>
    /// <param name="CurrentVersion">The room version now; later than <paramref name="Version"/> if the match moved on since.</param>
    /// <param name="Replayed">The action had already been applied; this is its stored result and nothing changed.</param>
    public sealed record MatchCommandResponse(Guid ActionId, string Type, long Version, long CurrentVersion, bool Replayed);

    /// <summary>Stable error codes; clients rely on these, never on the message text.</summary>
    public static class ErrorCodes
    {
        public const string ActionConflict = "action_conflict";
        public const string MatchNotStarted = "match_not_started";
        public const string NotInMatch = "not_in_match";
        public const string StaleVersion = "stale_version";
        public const string UnknownCommand = "unknown_command";
        public const string CommandRejected = "command_rejected";
        public const string NotYourTurn = "not_your_turn";
        public const string TurnExpired = "turn_expired";
        public const string TimeUp = "time_up";
        public const string RoundNotOpen = "round_not_open";
        public const string RoundClosed = "round_closed";
        public const string ActionLocked = "action_locked";
    }

    /// <summary>Resumes active matches at startup (DEC-030), before the turn scheduler (DEC-027) starts.</summary>
    public static IServiceCollection AddMatchTurns(this IServiceCollection services) =>
        services.AddHostedService<MatchRecoveryStartup>().AddHostedService<TurnTimeoutWorker>();

    public static IEndpointRouteBuilder MapMatchCommandEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/rooms/{code}/commands", SendCommandAsync)
            .RequireAuthorization()
            .RequireRateLimiting(RoomEndpoints.RateLimitPolicy)
            .WithName("SendMatchCommand");

        return endpoints;
    }

    private static async Task<Results<Ok<MatchCommandResponse>, ProblemHttpResult, ValidationProblem, NotFound>> SendCommandAsync(
        string code,
        MatchCommandRequest? request,
        ClaimsPrincipal user,
        ChogGamesDbContext db,
        MatchCommandService commands,
        RoomNotifier notifier,
        CancellationToken cancellationToken)
    {
        var playerId = Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var player = await db.Players.SingleOrDefaultAsync(p => p.Id == playerId, cancellationToken);
        if (player is null)
        {
            return TypedResults.NotFound();
        }

        if (Validate(request) is { } errors)
        {
            return TypedResults.ValidationProblem(errors);
        }

        if (!RoomCode.TryParse(code, out var roomCode))
        {
            return Failure(new MatchCommandResult(MatchCommandOutcome.RoomNotFound));
        }

        var command = new MatchCommand(
            request!.ActionId!.Value, request.MatchId!.Value, request.ExpectedVersion!.Value, request.Type!, request.Payload ?? EmptyPayload);
        var result = await commands.ExecuteAsync(player, roomCode, command, cancellationToken);
        if (result.Action is not { } action)
        {
            return Failure(result);
        }

        if (result.Outcome == MatchCommandOutcome.Applied)
        {
            await notifier.RoomChangedAsync(roomCode.Value);
        }

        return TypedResults.Ok(new MatchCommandResponse(
            action.ActionId, action.Type, action.Version, result.CurrentVersion!.Value, result.Outcome == MatchCommandOutcome.Replayed));
    }

    private static Dictionary<string, string[]>? Validate(MatchCommandRequest? request)
    {
        var errors = new Dictionary<string, string[]>();
        if (request?.ActionId is not { } actionId || actionId == Guid.Empty)
        {
            errors["actionId"] = ["An action id (a UUID) is required."];
        }

        if (request?.MatchId is not { } matchId || matchId == Guid.Empty)
        {
            errors["matchId"] = ["A match id (a UUID) is required."];
        }

        if (request?.ExpectedVersion is not >= 0)
        {
            errors["expectedVersion"] = ["The expected version must be zero or more."];
        }

        if (!MatchCommand.IsValidType(request?.Type))
        {
            errors["type"] = [$"The type must be upper-case letters, digits and underscores, at most {MatchCommand.MaxTypeLength} characters."];
        }

        if (request?.Payload is { } payload
            && (payload.ValueKind != JsonValueKind.Object || payload.GetRawText().Length > MatchCommand.MaxPayloadLength))
        {
            errors["payload"] = [$"The payload must be a JSON object of at most {MatchCommand.MaxPayloadLength} characters."];
        }

        return errors.Count > 0 ? errors : null;
    }

    private static ProblemHttpResult Failure(MatchCommandResult result)
    {
        var (status, code, detail) = result.Outcome switch
        {
            MatchCommandOutcome.RoomNotFound => (
                StatusCodes.Status404NotFound, RoomEndpoints.ErrorCodes.RoomNotFound, "No room with this code was found."),
            MatchCommandOutcome.ActionConflict => (
                StatusCodes.Status409Conflict, ErrorCodes.ActionConflict, "This action id was already used for another command."),
            MatchCommandOutcome.NotStarted => (
                StatusCodes.Status409Conflict, ErrorCodes.MatchNotStarted, "This room's game hasn't started yet."),
            MatchCommandOutcome.RoomClosed => (StatusCodes.Status409Conflict, RoomEndpoints.ErrorCodes.RoomClosed, "This room is closed."),
            MatchCommandOutcome.NotPlaying => (StatusCodes.Status403Forbidden, ErrorCodes.NotInMatch, "You have left this game."),
            MatchCommandOutcome.StaleVersion => (
                StatusCodes.Status409Conflict, ErrorCodes.StaleVersion, "The game has changed since this command was made. Refresh and try again."),
            MatchCommandOutcome.UnknownCommand => (
                StatusCodes.Status400BadRequest, ErrorCodes.UnknownCommand, "This game has no such command."),
            MatchCommandOutcome.NotYourTurn => (StatusCodes.Status409Conflict, ErrorCodes.NotYourTurn, "It isn't your turn."),
            MatchCommandOutcome.TurnExpired => (StatusCodes.Status409Conflict, ErrorCodes.TurnExpired, "Your turn has run out."),
            MatchCommandOutcome.TimeUp => (StatusCodes.Status409Conflict, ErrorCodes.TimeUp, "This game's time is up."),
            MatchCommandOutcome.RoundNotOpen => (
                StatusCodes.Status409Conflict, ErrorCodes.RoundNotOpen, "This round isn't open for actions yet."),
            MatchCommandOutcome.RoundClosed => (StatusCodes.Status409Conflict, ErrorCodes.RoundClosed, "This round's time has run out."),
            MatchCommandOutcome.ActionLocked => (
                StatusCodes.Status409Conflict, ErrorCodes.ActionLocked, "You've locked your action for this round."),
            MatchCommandOutcome.Rejected => (
                StatusCodes.Status409Conflict, ErrorCodes.CommandRejected, "This command isn't allowed right now."),
            _ => throw new ArgumentOutOfRangeException(nameof(result), result.Outcome, "Not a failure."),
        };

        var extensions = new Dictionary<string, object?> { ["code"] = code };
        if (result.CurrentVersion is { } currentVersion)
        {
            extensions["currentVersion"] = currentVersion;
        }

        if (result.Reason is not null)
        {
            extensions["reason"] = result.Reason;
        }

        return TypedResults.Problem(detail, statusCode: status, extensions: extensions);
    }
}
