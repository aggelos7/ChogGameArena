namespace ChogGames.Api.Realtime;

/// <summary>
/// Which hub connections of which player are subscribed to which room (STORY-022). SignalR groups can't be queried,
/// so this is what lets the server take a player who left out of the room's group. In memory: one API instance only;
/// several instances need a shared store together with a SignalR backplane (ADR-010).
/// </summary>
public sealed class RoomConnectionRegistry
{
    private readonly object _lock = new();
    private readonly Dictionary<string, Subscription> _byConnection = [];

    public void Add(string connectionId, Guid playerId, string roomCode)
    {
        lock (_lock)
        {
            if (!_byConnection.TryGetValue(connectionId, out var subscription))
            {
                subscription = new Subscription(playerId);
                _byConnection[connectionId] = subscription;
            }

            subscription.Rooms.Add(roomCode);
        }
    }

    public void Remove(string connectionId, string roomCode)
    {
        lock (_lock)
        {
            if (_byConnection.TryGetValue(connectionId, out var subscription) && subscription.Rooms.Remove(roomCode) && subscription.Rooms.Count == 0)
            {
                _byConnection.Remove(connectionId);
            }
        }
    }

    /// <summary>Forgets a closed connection; SignalR drops it from its groups by itself.</summary>
    public void RemoveConnection(string connectionId)
    {
        lock (_lock)
        {
            _byConnection.Remove(connectionId);
        }
    }

    /// <summary>Unregisters and returns the player's connections subscribed to the room.</summary>
    public IReadOnlyList<string> TakeConnections(Guid playerId, string roomCode)
    {
        lock (_lock)
        {
            var taken = _byConnection
                .Where(entry => entry.Value.PlayerId == playerId && entry.Value.Rooms.Contains(roomCode))
                .Select(entry => entry.Key)
                .ToList();
            foreach (var connectionId in taken)
            {
                Remove(connectionId, roomCode);
            }

            return taken;
        }
    }

    public bool IsSubscribed(string connectionId, string roomCode)
    {
        lock (_lock)
        {
            return _byConnection.TryGetValue(connectionId, out var subscription) && subscription.Rooms.Contains(roomCode);
        }
    }

    private sealed record Subscription(Guid PlayerId)
    {
        public HashSet<string> Rooms { get; } = new(StringComparer.Ordinal);
    }
}
