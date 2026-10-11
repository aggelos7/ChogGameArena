using System.Security.Claims;
using System.Threading.RateLimiting;
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
/// link) and read a room you are in. The player is always the session's; errors carry a stable <c>code</c> (DEC-021).
/// </summary>
public static class RoomEndpoints
{
    public const string RateLimitPolicy = "rooms";

    public sealed record CreateRoomRequest(string? GameType);

    public sealed record RoomMemberResponse(string DisplayName, string AvatarId, bool IsHost, bool IsYou);

    public sealed record RoomResponse(string Code, string GameType, RoomStatus Status, int Capacity, RoomMemberResponse[] Members);

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
    }

    public static IServiceCollection AddRooms(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<RoomOptions>()
            .Bind(configuration.GetSection(RoomOptions.SectionName))
            .Validate(o => o.RequestsPerMinutePerPlayer is >= 1 and <= 1000, "Rooms:RequestsPerMinutePerPlayer must be between 1 and 1000.")
            .ValidateOnStart();

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
        rooms.MapPost("/{code}/join", JoinRoomAsync).WithName("JoinRoom");
        rooms.MapGet("/{code}", GetRoomAsync).WithName("GetRoom");

        return endpoints;
    }

    private static async Task<Results<Created<RoomResponse>, ProblemHttpResult, NotFound>> CreateRoomAsync(
        CreateRoomRequest? request, ClaimsPrincipal user, ChogGamesDbContext db, RoomService rooms, CancellationToken cancellationToken)
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

        return TypedResults.Created($"/api/rooms/{room.Code}", await ToResponseAsync(room, player, db, cancellationToken));
    }

    private static async Task<Results<Ok<RoomResponse>, ProblemHttpResult, NotFound>> JoinRoomAsync(
        string code, ClaimsPrincipal user, ChogGamesDbContext db, RoomService rooms, CancellationToken cancellationToken)
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

        return TypedResults.Ok(await ToResponseAsync(room, player, db, cancellationToken));
    }

    private static async Task<Results<Ok<RoomResponse>, ProblemHttpResult, NotFound>> GetRoomAsync(
        string code, ClaimsPrincipal user, ChogGamesDbContext db, RoomService rooms, CancellationToken cancellationToken)
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

        return TypedResults.Ok(await ToResponseAsync(room, player, db, cancellationToken));
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
            _ => throw new ArgumentOutOfRangeException(nameof(result), result.Outcome, "Not a failure."),
        };

        var extensions = new Dictionary<string, object?> { ["code"] = code };
        if (result.OpenRoomCode is not null)
        {
            extensions["roomCode"] = result.OpenRoomCode;
        }

        return TypedResults.Problem(detail, statusCode: status, extensions: extensions);
    }

    private static async Task<RoomResponse> ToResponseAsync(GameRoom room, Player caller, ChogGamesDbContext db, CancellationToken cancellationToken)
    {
        var memberIds = room.Members.Select(m => m.PlayerId).ToArray();
        var players = await db.Players.AsNoTracking()
            .Where(p => memberIds.Contains(p.Id))
            .Select(p => new { p.Id, p.DisplayName, p.AvatarId })
            .ToDictionaryAsync(p => p.Id, cancellationToken);

        // Player ids stay on the server; members are described only by what the lobby shows.
        var members = room.Members
            .OrderBy(m => m.JoinedAt)
            .ThenBy(m => m.PlayerId != room.HostPlayerId)
            .Select(m => new RoomMemberResponse(
                players[m.PlayerId].DisplayName, players[m.PlayerId].AvatarId, m.PlayerId == room.HostPlayerId, m.PlayerId == caller.Id))
            .ToArray();

        return new RoomResponse(room.Code, room.GameType, room.Status, room.Capacity, members);
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

    /// <summary>Room requests (create, join, read) per player per minute.</summary>
    public int RequestsPerMinutePerPlayer { get; set; } = 60;
}
