namespace ChogGames.Api.Realtime;

/// <summary>
/// Which hub connections of which player are subscribed to which room (STORY-022). Each subscriber gets a snapshot of
/// their own (STORY-026, DEC-028), so this, not a SignalR group, decides who is sent what. In memory: one API instance
/// only; several instances need a shared store together with a SignalR backplane (ADR-010).
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

    /// <summary>Forgets a closed connection.</summary>
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

    /// <summary>The connections subscribed to the room, by player.</summary>
    public IReadOnlyDictionary<Guid, IReadOnlyList<string>> SubscribersOf(string roomCode)
    {
        lock (_lock)
        {
            return _byConnection
                .Where(entry => entry.Value.Rooms.Contains(roomCode))
                .GroupBy(entry => entry.Value.PlayerId, entry => entry.Key)
                .ToDictionary(group => group.Key, group => (IReadOnlyList<string>)group.ToList());
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
