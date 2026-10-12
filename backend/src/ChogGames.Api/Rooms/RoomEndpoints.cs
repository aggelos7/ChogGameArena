using System.Security.Claims;
using System.Text.Json;
using System.Threading.RateLimiting;
using ChogGames.Api.Realtime;
using ChogGames.Core.Players;
using ChogGames.Core.Rooms;
using ChogGames.Infrastructure.Persistence;
using ChogGames.Infrastructure.Rooms;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ChogGames.Api.Rooms;

/// <summary>
/// Private rooms (STORY-020, ENG-001): create a room for an enabled game, join one by its code (typed or from an invite
/// link) and read a room you are in. In the lobby (STORY-021, ENG-002) members mark ready and the host starts;
/// members may leave a waiting room, and the host leaving cancels it (STORY-063). Leaving a started match is a
/// forfeit, and a started room shows each member's locked Chog (STORY-023, DEC-025). <c>/current</c> finds the
/// player's open room, so they can return to it after a refresh or a lost connection (STORY-027, DEC-029).
/// The player is always the session's; errors carry a stable <c>code</c> (DEC-021, DEC-022). Every committed change
/// is pushed to the room's subscribed members over SignalR as their own versioned snapshot (STORY-022, STORY-026).
/// </summary>
public static class RoomEndpoints
{
    public const string RateLimitPolicy = "rooms";

    public sealed record CreateRoomRequest(string? GameType);

    public sealed record SetReadyRequest(bool? Ready);

    /// <summary>The member's selected Chog. <c>Color</c> is set for standard Chogs; NFTs show their token id.</summary>
    public sealed record RoomCharacterResponse(CharacterKind Kind, string Id, string Name, string? Color);

    /// <param name="Character">The selected Chog while waiting; the Chog locked at start once the match has started.</param>
    /// <param name="Ability">The chosen ability; always null until ability selection (STORY-033).</param>
    /// <param name="HasLeft">The member left the started match (DEC-025).</param>
    /// <param name="IsTurn">It is this member's turn in the active match (DEC-027).</param>
    public sealed record RoomMemberResponse(
        string DisplayName,
        string AvatarId,
        bool IsHost,
        bool IsYou,
        bool IsReady,
        RoomCharacterResponse Character,
        string? Ability,
        bool HasLeft,
        bool IsTurn);

    /// <param name="MatchId">The room's id, which match commands name (DEC-026); it identifies no player.</param>
    /// <param name="Version">Bumped by every change to the room; match commands must name the current one (DEC-026).</param>
    /// <param name="TurnNumber">The turn being played, from 1; 0 before the start (DEC-027).</param>
    /// <param name="TurnEndsAt">When the current turn runs out; null unless the match is active.</param>
    /// <param name="State">
    /// What this member may see of the game's state (DEC-028); null before the game's first command and for games
    /// without rules.
    /// </param>
    public sealed record RoomResponse(
        string Code,
        Guid MatchId,
        long Version,
        string GameType,
        RoomStatus Status,
        int MinPlayers,
        int Capacity,
        int TurnNumber,
        DateTimeOffset? TurnEndsAt,
        RoomMemberResponse[] Members,
        JsonElement? State);

    /// <summary>Stable error codes; clients rely on these, never on the message text.</summary>
    public static class ErrorCodes
    {
        public const string UnknownGame = "unknown_game";
        public const string GameUnavailable = "game_unavailable";
        public const string AlreadyInRoom = "already_in_room";
        public const string RoomNotFound = "room_not_found";
        public const string RoomFull = "room_full";
        public const string RoomStarted = "room_started";
        public const string RoomClosed = "room_closed";
        public const string NotHost = "not_host";
        public const string NotEnoughPlayers = "not_enough_players";
        public const string PlayersNotReady = "players_not_ready";
        public const string CharacterUnavailable = "character_unavailable";
        public const string OwnershipUnavailable = "ownership_unavailable";
    }

    public static IServiceCollection AddRooms(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<RoomOptions>()
            .Bind(configuration.GetSection(RoomOptions.SectionName))
            .Validate(o => o.RequestsPerMinutePerPlayer is >= 1 and <= 1000, "Rooms:RequestsPerMinutePerPlayer must be between 1 and 1000.")
            .ValidateOnStart();
        services.AddScoped<RoomSnapshots>();

        services.AddRateLimiter(options => options.AddPolicy(RateLimitPolicy, context =>
        {
            // Per player: limits guessing room codes as well as room spam.
            var permitLimit = context.RequestServices.GetRequiredService<IOptions<RoomOptions>>().Value.RequestsPerMinutePerPlayer;
            return RateLimitPartition.GetFixedWindowLimiter(
                context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "anonymous",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = permitLimit, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 });
        }));

        return services;
    }

    public static IEndpointRouteBuilder MapRoomEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var rooms = endpoints.MapGroup("/api/rooms").RequireAuthorization().RequireRateLimiting(RateLimitPolicy);

        rooms.MapPost("", CreateRoomAsync).WithName("CreateRoom");
        // A literal segment, so it is matched before "/{code}"; "current" is never a valid room code anyway.
        rooms.MapGet("/current", GetCurrentRoomAsync).WithName("GetCurrentRoom");
        rooms.MapPost("/{code}/join", JoinRoomAsync).WithName("JoinRoom");
        rooms.MapGet("/{code}", GetRoomAsync).WithName("GetRoom");
        rooms.MapPut("/{code}/ready", SetReadyAsync).WithName("SetRoomReady");
        rooms.MapPost("/{code}/start", StartRoomAsync).WithName("StartRoom");
        rooms.MapPost("/{code}/leave", LeaveRoomAsync).WithName("LeaveRoom");

        return endpoints;
    }

    private static async Task<Results<Created<RoomResponse>, ProblemHttpResult, NotFound>> CreateRoomAsync(
        CreateRoomRequest? request, ClaimsPrincipal user, ChogGamesDbContext db, RoomService rooms, RoomSnapshots snapshots, CancellationToken cancellationToken)
    {
        var player = await FindCurrentPlayerAsync(user, db, cancellationToken);
        if (player is null)
        {
            return TypedResults.NotFound();
        }

        var result = await rooms.CreateAsync(player, request?.GameType ?? string.Empty, cancellationToken);
        if (result.Room is not { } room)
        {
            return Failure(result);
        }

        return TypedResults.Created($"/api/rooms/{room.Code}", await snapshots.ForAsync(room, player.Id, cancellationToken));
    }

    private static async Task<Results<Ok<RoomResponse>, ProblemHttpResult, NotFound>> JoinRoomAsync(
        string code, ClaimsPrincipal user, ChogGamesDbContext db, RoomService rooms, RoomSnapshots snapshots, RoomNotifier notifier, CancellationToken cancellationToken)
    {
        var player = await FindCurrentPlayerAsync(user, db, cancellationToken);
        if (player is null)
        {
            return TypedResults.NotFound();
        }

        if (!RoomCode.TryParse(code, out var roomCode))
        {
            return Failure(new RoomResult(RoomOutcome.RoomNotFound));
        }

        var result = await rooms.JoinAsync(player, roomCode, cancellationToken);
        if (result.Room is not { } room)
        {
            return Failure(result);
        }

        if (result.Outcome == RoomOutcome.Joined)
        {
            await notifier.RoomChangedAsync(room.Code);
        }

        return TypedResults.Ok(await snapshots.ForAsync(room, player.Id, cancellationToken));
    }

    /// <summary>
    /// The player's open room (a waiting room they are in or a started match they still play), or 204 when there is
    /// none (STORY-027, DEC-029). Clients use it to bring the player back after a refresh or on another device.
    /// </summary>
    private static async Task<Results<Ok<RoomResponse>, NoContent, NotFound>> GetCurrentRoomAsync(
        ClaimsPrincipal user, ChogGamesDbContext db, RoomService rooms, RoomSnapshots snapshots, CancellationToken cancellationToken)
    {
        var player = await FindCurrentPlayerAsync(user, db, cancellationToken);
        if (player is null)
        {
            return TypedResults.NotFound();
        }

        var room = await rooms.FindOpenForPlayerAsync(player, cancellationToken);
        return room is null ? TypedResults.NoContent() : TypedResults.Ok(await snapshots.ForAsync(room, player.Id, cancellationToken));
    }

    private static async Task<Results<Ok<RoomResponse>, ProblemHttpResult, NotFound>> GetRoomAsync(
        string code, ClaimsPrincipal user, ChogGamesDbContext db, RoomService rooms, RoomSnapshots snapshots, CancellationToken cancellationToken)
    {
        var player = await FindCurrentPlayerAsync(user, db, cancellationToken);
        if (player is null)
        {
            return TypedResults.NotFound();
        }

        // Unknown codes and rooms the caller is not in look the same, so codes cannot be probed.
        var room = RoomCode.TryParse(code, out var roomCode) ? await rooms.FindForMemberAsync(player, roomCode, cancellationToken) : null;
        if (room is null)
        {
            return Failure(new RoomResult(RoomOutcome.RoomNotFound));
        }

        return TypedResults.Ok(await snapshots.ForAsync(room, player.Id, cancellationToken));
    }

    private static async Task<Results<Ok<RoomResponse>, ProblemHttpResult, ValidationProblem, NotFound>> SetReadyAsync(
        string code,
        SetReadyRequest? request,
        ClaimsPrincipal user,
        ChogGamesDbContext db,
        LobbyService lobby,
        RoomSnapshots snapshots,
        RoomNotifier notifier,
        CancellationToken cancellationToken)
    {
        var player = await FindCurrentPlayerAsync(user, db, cancellationToken);
        if (player is null)
        {
            return TypedResults.NotFound();
        }

        if (request?.Ready is not { } ready)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["ready"] = ["Ready must be true or false."] });
        }

        if (!RoomCode.TryParse(code, out var roomCode))
        {
            return Failure(new RoomResult(RoomOutcome.RoomNotFound));
        }

        var result = await lobby.SetReadyAsync(player, roomCode, ready, cancellationToken);
        if (result.Room is not { } room)
        {
            return Failure(result);
        }

        await notifier.RoomChangedAsync(room.Code);

        return TypedResults.Ok(await snapshots.ForAsync(room, player.Id, cancellationToken));
    }

    private static async Task<Results<Ok<RoomResponse>, ProblemHttpResult, NotFound>> StartRoomAsync(
        string code,
        ClaimsPrincipal user,
        ChogGamesDbContext db,
        LobbyService lobby,
        RoomSnapshots snapshots,
        RoomNotifier notifier,
        CancellationToken cancellationToken)
    {
        var player = await FindCurrentPlayerAsync(user, db, cancellationToken);
        if (player is null)
        {
            return TypedResults.NotFound();
        }

        if (!RoomCode.TryParse(code, out var roomCode))
        {
            return Failure(new RoomResult(RoomOutcome.RoomNotFound));
        }

        var result = await lobby.StartAsync(player, roomCode, cancellationToken);
        if (result.Outcome == RoomOutcome.CharacterUnavailable)
        {
            // Members whose NFT left their wallet were set back to not ready.
            await notifier.RoomChangedAsync(roomCode.Value);
        }

        if (result.Room is not { } room)
        {
            return Failure(result);
        }

        await notifier.RoomChangedAsync(room.Code);

        return TypedResults.Ok(await snapshots.ForAsync(room, player.Id, cancellationToken));
    }

    private static async Task<Results<NoContent, ProblemHttpResult, NotFound>> LeaveRoomAsync(
        string code, ClaimsPrincipal user, ChogGamesDbContext db, LobbyService lobby, RoomNotifier notifier, CancellationToken cancellationToken)
    {
        var player = await FindCurrentPlayerAsync(user, db, cancellationToken);
        if (player is null)
        {
            return TypedResults.NotFound();
        }

        if (!RoomCode.TryParse(code, out var roomCode))
        {
            return Failure(new RoomResult(RoomOutcome.RoomNotFound));
        }

        var result = await lobby.LeaveAsync(player, roomCode, cancellationToken);
        if (result.Room is not { } room)
        {
            return Failure(result);
        }

        // A host who leaves stays subscribed to the cancelled room; anyone else who leaves stops getting its updates.
        if (result.Outcome == RoomOutcome.Left)
        {
            await notifier.RoomChangedAsync(room.Code, room.IsPlaying(player.Id) ? null : player.Id);
        }

        return TypedResults.NoContent();
    }

    private static ProblemHttpResult Failure(RoomResult result)
    {
        var (status, code, detail) = result.Outcome switch
        {
            RoomOutcome.UnknownGame => (StatusCodes.Status400BadRequest, ErrorCodes.UnknownGame, "This game does not exist."),
            RoomOutcome.GameUnavailable => (StatusCodes.Status409Conflict, ErrorCodes.GameUnavailable, "This game is not available right now."),
            RoomOutcome.AlreadyInRoom => (StatusCodes.Status409Conflict, ErrorCodes.AlreadyInRoom, "You are already in a room."),
            RoomOutcome.RoomFull => (StatusCodes.Status409Conflict, ErrorCodes.RoomFull, "This room is full."),
            RoomOutcome.RoomStarted => (StatusCodes.Status409Conflict, ErrorCodes.RoomStarted, "This room's game has already started."),
            RoomOutcome.RoomClosed => (StatusCodes.Status409Conflict, ErrorCodes.RoomClosed, "This room is closed."),
            RoomOutcome.RoomNotFound => (StatusCodes.Status404NotFound, ErrorCodes.RoomNotFound, "No room with this code was found."),
            RoomOutcome.NotHost => (StatusCodes.Status403Forbidden, ErrorCodes.NotHost, "Only the host can start the game."),
            RoomOutcome.NotEnoughPlayers => (StatusCodes.Status409Conflict, ErrorCodes.NotEnoughPlayers, "More players are needed to start."),
            RoomOutcome.PlayersNotReady => (StatusCodes.Status409Conflict, ErrorCodes.PlayersNotReady, "Every player must be ready to start."),
            RoomOutcome.CharacterUnavailable => (
                StatusCodes.Status409Conflict, ErrorCodes.CharacterUnavailable, "A player no longer owns their chosen Chog and must choose again."),
            RoomOutcome.OwnershipUnavailable => (
                StatusCodes.Status503ServiceUnavailable, ErrorCodes.OwnershipUnavailable, "Chog ownership can't be checked right now. Try again shortly."),
            _ => throw new ArgumentOutOfRangeException(nameof(result), result.Outcome, "Not a failure."),
        };

        var extensions = new Dictionary<string, object?> { ["code"] = code };
        if (result.OpenRoomCode is not null)
        {
            extensions["roomCode"] = result.OpenRoomCode;
        }

        return TypedResults.Problem(detail, statusCode: status, extensions: extensions);
    }

    private static Task<Player?> FindCurrentPlayerAsync(ClaimsPrincipal user, ChogGamesDbContext db, CancellationToken cancellationToken)
    {
        var playerId = Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);
        return db.Players.SingleOrDefaultAsync(p => p.Id == playerId, cancellationToken);
    }
}

/// <summary>Room API settings bound from the <c>Rooms</c> configuration section (non-secret).</summary>
public sealed class RoomOptions
{
    public const string SectionName = "Rooms";

    /// <summary>
    /// Room requests (create, join, read, ready, start, leave, match commands, opening a live connection) per player per minute; calls
    /// over a live connection have their own budget of the same size.
    /// </summary>
    public int RequestsPerMinutePerPlayer { get; set; } = 60;
}
