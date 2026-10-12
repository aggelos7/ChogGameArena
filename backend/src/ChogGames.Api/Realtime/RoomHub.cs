using System.Security.Claims;
using ChogGames.Api.Rooms;
using static ChogGames.Api.Rooms.RoomEndpoints;
using ChogGames.Core.Rooms;
using ChogGames.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace ChogGames.Api.Realtime;

/// <summary>Messages the server sends to hub clients.</summary>
public interface IRoomClient
{
    /// <summary>
    /// The room changed: this member's snapshot of it, as <c>GET /api/rooms/{code}</c> would return it now (DEC-028).
    /// Clients keep the snapshot with the highest version and ignore older ones.
    /// </summary>
    Task RoomUpdated(RoomResponse room);
}

/// <summary>
/// Live room updates (STORY-022, ENG-005). Connections are authenticated by the session cookie like every other
/// request; the player is always the session's (<see cref="HubCallerContext.UserIdentifier"/>), never a client value.
/// A connection is only subscribed to a room while its player plays in it, and updates are only sent to players who
/// do when they are sent (<see cref="RoomNotifier"/>), so room messages only reach members. Unknown codes and
/// rooms the caller isn't in both fail with <c>room_not_found</c>, so codes can't be probed. Room changes and match
/// commands go through the REST endpoints. Every change is pushed as each member's own versioned snapshot, and
/// subscribing returns the current one, so a client that missed updates catches up there (STORY-026, DEC-028).
/// </summary>
[Authorize]
public sealed class RoomHub(ChogGamesDbContext db, RoomConnectionRegistry connections, RoomSnapshots snapshots) : Hub<IRoomClient>
{
    public const string Path = "/api/hubs/rooms";

    /// <summary>Stable error codes sent as the <see cref="HubException"/> message; clients rely on these.</summary>
    public static class ErrorCodes
    {
        public const string RoomNotFound = "room_not_found";
        public const string RateLimited = "rate_limited";
    }

    /// <summary>
    /// Starts sending this connection <see cref="IRoomClient.RoomUpdated"/> for a room its player is in, and returns the
    /// player's current snapshot of it, from which later updates follow.
    /// </summary>
    public async Task<RoomResponse> SubscribeRoom(string code)
    {
        var playerId = Guid.Parse(Context.User!.FindFirstValue(ClaimTypes.NameIdentifier)!);
        if (!RoomCode.TryParse(code, out var roomCode))
        {
            throw new HubException(ErrorCodes.RoomNotFound);
        }

        // Registered before the room is read, so a change that commits in between is either in the snapshot or pushed
        // afterwards. Updates only go to players who are members when they are sent, so nothing reaches a non-member
        // while this is checked. A fresh read: a player who left a started match is no longer subscribed (DEC-025).
        connections.Add(Context.ConnectionId, playerId, roomCode.Value);
        var room = await db.GameRooms.AsNoTracking().SingleOrDefaultAsync(r => r.Code == roomCode.Value, Context.ConnectionAborted);
        if (room is null || !room.IsPlaying(playerId))
        {
            connections.Remove(Context.ConnectionId, roomCode.Value);
            throw new HubException(ErrorCodes.RoomNotFound);
        }

        return await snapshots.ForAsync(room, playerId, Context.ConnectionAborted);
    }

    /// <summary>Stops room messages for this connection; always succeeds.</summary>
    public Task UnsubscribeRoom(string code)
    {
        if (RoomCode.TryParse(code, out var roomCode))
        {
            connections.Remove(Context.ConnectionId, roomCode.Value);
        }

        return Task.CompletedTask;
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        connections.RemoveConnection(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }
}
