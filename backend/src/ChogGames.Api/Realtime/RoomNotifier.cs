using ChogGames.Api.Rooms;
using ChogGames.Infrastructure.Persistence;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace ChogGames.Api.Realtime;

/// <summary>
/// Pushes a room's change to its subscribed members (STORY-022), each as their own versioned snapshot (STORY-026,
/// DEC-028). Called after the change has been committed; the room is read again, so every member gets the same,
/// latest version, and only players who are members at that point get anything. A failure to push is logged and never
/// fails the request: clients catch up when they subscribe again.
/// </summary>
public sealed class RoomNotifier(
    IHubContext<RoomHub, IRoomClient> hub, RoomConnectionRegistry connections, IServiceScopeFactory scopes, ILogger<RoomNotifier> logger)
{
    /// <param name="code">The room's code.</param>
    /// <param name="departedPlayerId">
    /// A player who is no longer playing. If they can still read the room (a forfeited match) their connections get this
    /// last snapshot, showing them out; then they are unsubscribed, so later updates reach players only.
    /// </param>
    public async Task RoomChangedAsync(string code, Guid? departedPlayerId = null)
    {
        try
        {
            // Not the request's token: the change is committed, so its update and the clean-up must go out regardless.
            await PushSnapshotsAsync(code, departedPlayerId);
        }
        catch (Exception error)
        {
            logger.LogWarning(error, "Room update could not be pushed.");
        }

        if (departedPlayerId is { } playerId)
        {
            connections.TakeConnections(playerId, code);
        }
    }

    private async Task PushSnapshotsAsync(string code, Guid? departedPlayerId)
    {
        var subscribers = connections.SubscribersOf(code);
        if (subscribers.Count == 0)
        {
            return;
        }

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ChogGamesDbContext>();
        var room = await db.GameRooms.AsNoTracking().SingleOrDefaultAsync(r => r.Code == code);
        if (room is null)
        {
            return;
        }

        var viewers = subscribers.Keys
            .Where(playerId => room.IsPlaying(playerId) || (playerId == departedPlayerId && room.IsMember(playerId)))
            .ToList();
        var snapshots = await scope.ServiceProvider.GetRequiredService<RoomSnapshots>().ForEachAsync(room, viewers, CancellationToken.None);
        foreach (var viewer in viewers)
        {
            await hub.Clients.Clients(subscribers[viewer]).RoomUpdated(snapshots[viewer]);
        }
    }
}
