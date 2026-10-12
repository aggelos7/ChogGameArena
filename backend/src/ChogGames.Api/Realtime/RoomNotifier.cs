using Microsoft.AspNetCore.SignalR;

namespace ChogGames.Api.Realtime;

/// <summary>
/// Tells a room's subscribed members that it changed (STORY-022). Called after the change has been committed; a
/// failure to notify is logged and never fails the request, since clients still fetch the room's state over REST.
/// </summary>
public sealed class RoomNotifier(IHubContext<RoomHub, IRoomClient> hub, RoomConnectionRegistry connections, ILogger<RoomNotifier> logger)
{
    /// <param name="code">The room's code.</param>
    /// <param name="departedPlayerId">
    /// A player who is no longer a member. Their connections hear of this change too, then leave the room's group, so
    /// later messages reach members only.
    /// </param>
    public async Task RoomChangedAsync(string code, Guid? departedPlayerId = null)
    {
        var group = RoomHub.GroupName(code);
        try
        {
            // Not the request's token: the change is committed, so its notice and the clean-up must go out regardless.
            await hub.Clients.Group(group).RoomChanged(code);
        }
        catch (Exception error)
        {
            logger.LogWarning(error, "Room change notice could not be sent.");
        }

        if (departedPlayerId is not { } playerId)
        {
            return;
        }

        foreach (var connectionId in connections.TakeConnections(playerId, code))
        {
            await hub.Groups.RemoveFromGroupAsync(connectionId, group);
        }
    }
}
