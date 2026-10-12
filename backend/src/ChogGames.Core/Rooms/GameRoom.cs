using ChogGames.Core.Games;
using ChogGames.Core.Players;

namespace ChogGames.Core.Rooms;

/// <summary>
/// A private room for one game (STORY-019, ENG-001). The host is the creating player and its first member;
/// capacity and the minimum to start come from the game definition. While waiting it is the lobby (STORY-021, ENG-002):
/// members mark ready with their chosen Chog, and the host starts once enough members are ready. Once started the room
/// is the match (STORY-023, ENG-010, DEC-025): its members are the match players, each with their character snapshot,
/// and every status change goes through <see cref="RoomLifecycle"/>.
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

    /// <summary>When the room reached a final status (Completed, Cancelled or Abandoned); null while open.</summary>
    public DateTimeOffset? EndedAt { get; private set; }

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

    /// <summary>Has a seat record here, including a player who left a started match (they can still read it).</summary>
    public bool IsMember(Guid playerId) => _members.Any(m => m.PlayerId == playerId);

    /// <summary>A member who left the started match (DEC-025); they are out of it but keep their record.</summary>
    public bool HasLeft(Guid playerId) => _members.Any(m => m.PlayerId == playerId && m.HasLeft);

    /// <summary>A member who has not left: who gets the room's updates and, while it is open, can't be in another room.</summary>
    public bool IsPlaying(Guid playerId) => _members.Any(m => m.PlayerId == playerId && !m.HasLeft);

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

    /// <summary>
    /// <paramref name="playerId"/> leaves. In a waiting room (STORY-063, DEC-023) a member is removed, freeing their
    /// seat, and nobody else's ready status changes; when the host leaves, the room is cancelled for everyone and its
    /// members stay listed, so they can see it was closed. In a started match leaving is a forfeit (DEC-025): the member
    /// stays listed as left, and when fewer than two players remain the match is abandoned.
    /// </summary>
    public void Leave(Guid playerId, DateTimeOffset now)
    {
        if (Status == RoomStatus.Active)
        {
            var player = _members.SingleOrDefault(m => m.PlayerId == playerId && !m.HasLeft)
                ?? throw new InvalidOperationException("The player is not playing in this match.");
            player.Forfeit(now);
            if (_members.Count(m => !m.HasLeft) < GameDefinition.MinRoomPlayers)
            {
                MoveTo(RoomStatus.Abandoned, now);
            }

            return;
        }

        var member = MemberForChange(playerId);
        if (playerId == HostPlayerId)
        {
            MoveTo(RoomStatus.Cancelled, now);
            return;
        }

        _members.Remove(member);
    }

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
    /// Starts the match: the room becomes Active and each member's character is locked with the snapshot taken for
    /// them (DEC-019, DEC-025). The caller has checked <see cref="CheckStart"/> and verified every member's Chog
    /// (STORY-018); <paramref name="characters"/> holds a snapshot for every member.
    /// </summary>
    public void Start(
        Guid callerId,
        IReadOnlyCollection<Player> players,
        IReadOnlyDictionary<Guid, CharacterSnapshot> characters,
        DateTimeOffset now)
    {
        if (CheckStart(callerId, players) is { } problem)
        {
            throw new InvalidOperationException($"The room cannot start: {problem}.");
        }

        if (_members.Any(m => !characters.ContainsKey(m.PlayerId)))
        {
            throw new InvalidOperationException("Every member needs a character snapshot to start.");
        }

        MoveTo(RoomStatus.Active, now);
        StartedAt = now;
        foreach (var member in _members)
        {
            member.LockCharacter(characters[member.PlayerId]);
        }
    }

    /// <summary>The game finished the match (ENG-010). Called by game rules, which decide the results (later stories).</summary>
    public void Complete(DateTimeOffset now) => MoveTo(RoomStatus.Completed, now);

    /// <summary>The only place the status changes; anything <see cref="RoomLifecycle"/> does not allow is refused unchanged.</summary>
    private void MoveTo(RoomStatus next, DateTimeOffset now)
    {
        if (!RoomLifecycle.CanMove(Status, next))
        {
            throw new InvalidOperationException($"A room cannot go from {Status} to {next}.");
        }

        Status = next;
        if (RoomLifecycle.IsFinal(next))
        {
            EndedAt = now;
        }
    }

    private RoomMember MemberForChange(Guid playerId)
    {
        if (Status != RoomStatus.Waiting)
        {
            throw new InvalidOperationException("Members can only change in a waiting room.");
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

    /// <summary>The character locked when the match started (DEC-019, DEC-025); null before the start.</summary>
    public CharacterSnapshot? Character { get; private set; }

    /// <summary>When the member left the started match (DEC-025); null while they play.</summary>
    public DateTimeOffset? LeftAt { get; private set; }

    public bool HasLeft => LeftAt is not null;

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

    internal void LockCharacter(CharacterSnapshot character) => Character = character;

    internal void Forfeit(DateTimeOffset now) => LeftAt = now;
}

/// <summary>Room and match lifecycle (ENG-010, DEC-025); <see cref="RoomLifecycle"/> lists the allowed transitions.</summary>
public enum RoomStatus
{
    /// <summary>The lobby: players join, get ready and leave.</summary>
    Waiting,

    /// <summary>The match is being played.</summary>
    Active,

    /// <summary>The game finished the match.</summary>
    Completed,

    /// <summary>The host closed the room before it started.</summary>
    Cancelled,

    /// <summary>The match started but ended early: fewer than two players remained.</summary>
    Abandoned,
}

/// <summary>
/// The authoritative room state machine (STORY-023, DEC-025): Waiting → Active (start), Waiting → Cancelled (the host
/// leaves), Active → Completed (the game finishes), Active → Abandoned (fewer than two players remain). Completed,
/// Cancelled and Abandoned are final.
/// </summary>
public static class RoomLifecycle
{
    public static bool CanMove(RoomStatus from, RoomStatus to) => (from, to) switch
    {
        (RoomStatus.Waiting, RoomStatus.Active or RoomStatus.Cancelled) => true,
        (RoomStatus.Active, RoomStatus.Completed or RoomStatus.Abandoned) => true,
        _ => false,
    };

    public static bool IsFinal(RoomStatus status) => status is RoomStatus.Completed or RoomStatus.Cancelled or RoomStatus.Abandoned;
}
