using ChogGames.Core.Games;
using ChogGames.Core.Players;

namespace ChogGames.Core.Rooms;

/// <summary>
/// A private room for one game (STORY-019, ENG-001). The host is the creating player and its first member;
/// capacity and the minimum to start come from the game definition. While waiting it is the lobby (STORY-021, ENG-002):
/// members mark ready with their chosen Chog, and the host starts once enough members are ready.
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

    /// <summary>Members needed to start, from the game definition.</summary>
    public int MinPlayers { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? StartedAt { get; private set; }

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
            MinPlayers = game.MinPlayers,
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

    /// <summary>
    /// Marks <paramref name="player"/> ready with the Chog they have selected now. Choosing another Chog afterwards
    /// cancels it, since ready only counts for that Chog (DEC-022).
    /// </summary>
    public void MarkReady(Player player)
    {
        MemberForChange(player.Id).MarkReady(player.SelectedCharacterKind, player.SelectedCharacterId);
    }

    public void MarkNotReady(Guid playerId) => MemberForChange(playerId).MarkNotReady();

    /// <summary>Why the host can't start now, or null if they can. <paramref name="players"/> are the members' current players.</summary>
    public RoomStartProblem? CheckStart(Guid callerId, IReadOnlyCollection<Player> players)
    {
        if (callerId != HostPlayerId)
        {
            return RoomStartProblem.NotHost;
        }

        if (Status != RoomStatus.Waiting)
        {
            return RoomStartProblem.NotWaiting;
        }

        if (_members.Count < MinPlayers || _members.Count > Capacity)
        {
            return RoomStartProblem.NotEnoughPlayers;
        }

        var byId = players.ToDictionary(p => p.Id);
        return _members.All(m => byId.TryGetValue(m.PlayerId, out var player) && m.IsReadyWith(player))
            ? null
            : RoomStartProblem.PlayersNotReady;
    }

    /// <summary>
    /// Starts the match: the room becomes Active. The caller has checked <see cref="CheckStart"/> and verified every
    /// member's Chog (STORY-018).
    /// </summary>
    public void Start(Guid callerId, IReadOnlyCollection<Player> players, DateTimeOffset now)
    {
        if (CheckStart(callerId, players) is { } problem)
        {
            throw new InvalidOperationException($"The room cannot start: {problem}.");
        }

        Status = RoomStatus.Active;
        StartedAt = now;
    }

    private RoomMember MemberForChange(Guid playerId)
    {
        if (Status != RoomStatus.Waiting)
        {
            throw new InvalidOperationException("Ready status can only change in a waiting room.");
        }

        return _members.SingleOrDefault(m => m.PlayerId == playerId)
            ?? throw new InvalidOperationException("The player is not a member of this room.");
    }
}

public enum RoomStartProblem
{
    NotHost,
    NotWaiting,

    /// <summary>Fewer members than the game's minimum.</summary>
    NotEnoughPlayers,

    /// <summary>A member is not ready, or changed Chog after marking ready.</summary>
    PlayersNotReady,
}

/// <summary>A player's membership of a room, with their ready status.</summary>
public sealed class RoomMember
{
    public Guid RoomId { get; private set; }

    public Guid PlayerId { get; private set; }

    public DateTimeOffset JoinedAt { get; private set; }

    /// <summary>The Chog the member marked ready with; null when not ready.</summary>
    public CharacterKind? ReadyCharacterKind { get; private set; }

    public string? ReadyCharacterId { get; private set; }

    /// <summary>Ready, and still with the Chog <paramref name="player"/> has selected now.</summary>
    public bool IsReadyWith(Player player) =>
        player.Id == PlayerId
        && ReadyCharacterKind == player.SelectedCharacterKind
        && string.Equals(ReadyCharacterId, player.SelectedCharacterId, StringComparison.Ordinal);

    internal RoomMember(Guid roomId, Guid playerId, DateTimeOffset joinedAt)
    {
        RoomId = roomId;
        PlayerId = playerId;
        JoinedAt = joinedAt;
    }

    internal void MarkReady(CharacterKind kind, string characterId)
    {
        ReadyCharacterKind = kind;
        ReadyCharacterId = characterId;
    }

    internal void MarkNotReady()
    {
        ReadyCharacterKind = null;
        ReadyCharacterId = null;
    }
}

/// <summary>Room lifecycle (architecture: Waiting -> Active -> Completed; cancellation by explicit policy). Start (Waiting -> Active) is STORY-021; the full state machine is STORY-023.</summary>
public enum RoomStatus
{
    Waiting,
    Active,
    Completed,
    Cancelled,
}
