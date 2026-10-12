using System.Security.Claims;
using ChogGames.Core.Rooms;
using ChogGames.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace ChogGames.Api.Realtime;

/// <summary>Messages the server sends to hub clients.</summary>
public interface IRoomClient
{
    /// <summary>The room changed; clients fetch <c>GET /api/rooms/{code}</c> for its current state (DEC-024).</summary>
    Task RoomChanged(string code);
}

/// <summary>
/// Live room updates (STORY-022, ENG-005). Connections are authenticated by the session cookie like every other
/// request; the player is always the session's (<see cref="HubCallerContext.UserIdentifier"/>), never a client value.
/// A connection only joins a room's group after the server has checked that its player is a member, and players who
/// leave are taken out again (<see cref="RoomNotifier"/>), so room messages only reach members. Unknown codes and
/// rooms the caller isn't in both fail with <c>room_not_found</c>, so codes can't be probed. Room changes themselves
/// still go through the REST endpoints; game commands come with STORY-024.
/// </summary>
[Authorize]
public sealed class RoomHub(ChogGamesDbContext db, RoomConnectionRegistry connections) : Hub<IRoomClient>
{
    public const string Path = "/api/hubs/rooms";

    /// <summary>Stable error codes sent as the <see cref="HubException"/> message; clients rely on these.</summary>
    public static class ErrorCodes
    {
        public const string RoomNotFound = "room_not_found";
        public const string RateLimited = "rate_limited";
    }

    public static string GroupName(string roomCode) => $"room:{roomCode}";

    /// <summary>Starts sending this connection <see cref="IRoomClient.RoomChanged"/> for a room its player is in.</summary>
    public async Task SubscribeRoom(string code)
    {
        var playerId = Guid.Parse(Context.User!.FindFirstValue(ClaimTypes.NameIdentifier)!);
        if (!RoomCode.TryParse(code, out var roomCode) || !await IsMemberAsync(playerId, roomCode))
        {
            throw new HubException(ErrorCodes.RoomNotFound);
        }

        // Registered before joining the group and checked again after, so a leave that commits in between either
        // finds this connection to remove it or is seen here.
        connections.Add(Context.ConnectionId, playerId, roomCode.Value);
        await Groups.AddToGroupAsync(Context.ConnectionId, GroupName(roomCode.Value));
        if (!await IsMemberAsync(playerId, roomCode))
        {
            await RemoveAsync(roomCode.Value);
            throw new HubException(ErrorCodes.RoomNotFound);
        }
    }

    /// <summary>Stops room messages for this connection; always succeeds.</summary>
    public async Task UnsubscribeRoom(string code)
    {
        if (RoomCode.TryParse(code, out var roomCode))
        {
            await RemoveAsync(roomCode.Value);
        }
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        connections.RemoveConnection(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }

    private async Task RemoveAsync(string roomCode)
    {
        connections.Remove(Context.ConnectionId, roomCode);
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupName(roomCode));
    }

    /// <summary>
    /// A fresh read each time: membership may have changed since the last one. A player who left a started match is
    /// no longer a member here (DEC-025).
    /// </summary>
    private Task<bool> IsMemberAsync(Guid playerId, RoomCode code) =>
        db.GameRooms.AsNoTracking().AnyAsync(
            r => r.Code == code.Value && r.Members.Any(m => m.PlayerId == playerId && m.LeftAt == null), Context.ConnectionAborted);
}
