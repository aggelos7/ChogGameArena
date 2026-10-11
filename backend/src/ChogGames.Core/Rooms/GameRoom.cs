using ChogGames.Core.Games;
using ChogGames.Core.Players;

namespace ChogGames.Core.Rooms;

/// <summary>
/// A private room for one game (STORY-019, ENG-001). The host is the creating player and its first member;
/// capacity comes from the game definition and never exceeds six players.
/// </summary>
public sealed class GameRoom
{
    private readonly List<RoomMember> _members = [];

    public Guid Id { get; private set; }

    /// <summary>Unique invite code; see <see cref="RoomCode"/>.</summary>
    public string Code { get; private set; } = string.Empty;

    /// <summary>Stable game type of a registered <see cref="GameDefinition"/>.</summary>
    public string GameType { get; private set; } = string.Empty;

    public Guid HostPlayerId { get; private set; }

    public RoomStatus Status { get; private set; }

    public int Capacity { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public IReadOnlyList<RoomMember> Members => _members;

    private GameRoom()
    {
    }

    /// <summary>A new waiting room hosted by <paramref name="host"/>. The caller has checked that the game is enabled.</summary>
    public static GameRoom Create(GameDefinition game, Player host, RoomCode code, DateTimeOffset now)
    {
        var room = new GameRoom
        {
            Id = Guid.NewGuid(),
            Code = code.Value,
            GameType = game.Id,
            HostPlayerId = host.Id,
            Status = RoomStatus.Waiting,
            Capacity = game.MaxPlayers,
            CreatedAt = now,
        };
        room._members.Add(new RoomMember(room.Id, host.Id, now));
        return room;
    }

    public bool IsMember(Guid playerId) => _members.Any(m => m.PlayerId == playerId);

    public bool IsFull => _members.Count >= Capacity;

    /// <summary>
    /// Adds <paramref name="player"/> to a waiting room with a free seat. The caller has checked that the player is
    /// in no other open room (DEC-021).
    /// </summary>
    public void Join(Player player, DateTimeOffset now)
    {
        if (Status != RoomStatus.Waiting)
        {
            throw new InvalidOperationException("Only a waiting room can be joined.");
        }

        if (IsMember(player.Id))
        {
            throw new InvalidOperationException("The player is already a member of this room.");
        }

        if (IsFull)
        {
            throw new InvalidOperationException("The room is full.");
        }

        _members.Add(new RoomMember(Id, player.Id, now));
    }
}

/// <summary>A player's membership of a room.</summary>
public sealed class RoomMember
{
    public Guid RoomId { get; private set; }

    public Guid PlayerId { get; private set; }

    public DateTimeOffset JoinedAt { get; private set; }

    internal RoomMember(Guid roomId, Guid playerId, DateTimeOffset joinedAt)
    {
        RoomId = roomId;
        PlayerId = playerId;
        JoinedAt = joinedAt;
    }
}

/// <summary>Room lifecycle (architecture: Waiting -> Active -> Completed; cancellation by explicit policy). Transitions are STORY-023.</summary>
public enum RoomStatus
{
    Waiting,
    Active,
    Completed,
    Cancelled,
}
