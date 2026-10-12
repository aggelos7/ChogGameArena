using ChogGames.Core.Games;
using ChogGames.Core.Matches;
using ChogGames.Core.Players;

namespace ChogGames.Core.Rooms;

/// <summary>
/// A private room for one game (STORY-019, ENG-001). The host is the creating player and its first member;
/// capacity and the minimum to start come from the game definition. While waiting it is the lobby (STORY-021, ENG-002):
/// members mark ready with their chosen Chog, and the host starts once enough members are ready. Once started the room
/// is the match (STORY-023, ENG-010, DEC-025): its members are the match players, each with their character snapshot,
/// and every status change goes through <see cref="RoomLifecycle"/>. Every change bumps <see cref="Version"/>, and players act
/// on the match through versioned, idempotent commands (STORY-024, DEC-026). The engine owns the turn (STORY-025, DEC-027):
/// players take turns in seat order, only the current player may act, and a turn that runs out is timed out once. Every
/// change is committed as it happens, so a restart resumes the match (STORY-028, DEC-030); a completed match keeps its
/// final standings on the members.
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

    /// <summary>
    /// Bumped by every committed change: join, ready, leave, start, completion and each accepted command (DEC-026).
    /// A command must name the current version, so one built on older state is refused.
    /// </summary>
    public long Version { get; private set; }

    /// <summary>The game's own match state as a JSON object, owned by its rules; null until their first accepted command.</summary>
    public string? State { get; private set; }

    /// <summary>How long each turn of this match lasts, fixed from configuration at the start (DEC-027); null before it.</summary>
    public TimeSpan? TurnTimeout { get; private set; }

    /// <summary>The turn being played, counted from 1 at the start; 0 before it. Kept once the match ends.</summary>
    public int TurnNumber { get; private set; }

    /// <summary>Whose turn it is; set exactly while the match is Active.</summary>
    public Guid? CurrentPlayerId { get; private set; }

    /// <summary>When the current turn runs out (ENG-007); set exactly while the match is Active.</summary>
    public DateTimeOffset? TurnEndsAt { get; private set; }

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

    /// <summary>The current turn ran out and has not been timed out yet.</summary>
    public bool IsTurnOverdue(DateTimeOffset now) => Status == RoomStatus.Active && now >= TurnEndsAt;

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
        Version++;
    }

    /// <summary>
    /// Marks <paramref name="player"/> ready with the Chog they have selected now. Choosing another Chog afterwards
    /// cancels it, since ready only counts for that Chog (DEC-022).
    /// </summary>
    public void MarkReady(Player player)
    {
        if (MemberForChange(player.Id).MarkReady(player.SelectedCharacterKind, player.SelectedCharacterId))
        {
            Version++;
        }
    }

    public void MarkNotReady(Guid playerId)
    {
        if (MemberForChange(playerId).MarkNotReady())
        {
            Version++;
        }
    }

    /// <summary>
    /// <paramref name="playerId"/> leaves. In a waiting room (STORY-063, DEC-023) a member is removed, freeing their
    /// seat, and nobody else's ready status changes; when the host leaves, the room is cancelled for everyone and its
    /// members stay listed, so they can see it was closed. In a started match leaving is a forfeit (DEC-025): the member
    /// stays listed as left, and when fewer than two players remain the match is abandoned.
    /// </summary>
    public void Leave(Guid playerId, DateTimeOffset now)
    {
        LeaveRoom(playerId, now);
        Version++;
    }

    private void LeaveRoom(Guid playerId, DateTimeOffset now)
    {
        if (Status == RoomStatus.Active)
        {
            var player = _members.SingleOrDefault(m => m.PlayerId == playerId && !m.HasLeft)
                ?? throw new InvalidOperationException("The player is not playing in this match.");
            Forfeit(player, now);
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

    /// <summary>
    /// <paramref name="player"/> is out of the match: when fewer than two players remain it is abandoned, and when it was
    /// their turn the next player's begins (DEC-027).
    /// </summary>
    private void Forfeit(RoomMember player, DateTimeOffset now)
    {
        player.Forfeit(now);
        if (_members.Count(m => !m.HasLeft) < GameDefinition.MinRoomPlayers)
        {
            MoveTo(RoomStatus.Abandoned, now);
        }
        else if (player.PlayerId == CurrentPlayerId)
        {
            NextTurn(now);
        }
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
    /// them (DEC-019, DEC-025). Members are seated in turn order, the host first and then in the order they joined, and
    /// the first turn begins with <paramref name="turnTimeout"/> to play it (DEC-027). The caller has checked
    /// <see cref="CheckStart"/> and verified every member's Chog (STORY-018); <paramref name="characters"/> holds a
    /// snapshot for every member.
    /// </summary>
    public void Start(
        Guid callerId,
        IReadOnlyCollection<Player> players,
        IReadOnlyDictionary<Guid, CharacterSnapshot> characters,
        TimeSpan turnTimeout,
        DateTimeOffset now)
    {
        if (turnTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(turnTimeout), turnTimeout, "A turn must last some time.");
        }

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

        var seat = 0;
        foreach (var member in _members.OrderBy(m => m.PlayerId != HostPlayerId).ThenBy(m => m.JoinedAt).ThenBy(m => m.PlayerId))
        {
            member.TakeSeat(++seat);
        }

        TurnTimeout = turnTimeout;
        BeginTurn(_members.Single(m => m.Seat == 1), now);
        Version++;
    }

    /// <summary>
    /// The game finished the match (ENG-010, STORY-028, DEC-030) with <paramref name="standings"/>: a rank for every
    /// player still playing, as its rules decided. Players who left are ranked after them, the latest to leave highest.
    /// The ranks are stored on the members once, with the move to Completed; a finished match can't be completed again.
    /// </summary>
    public void Complete(IReadOnlyCollection<MatchStanding> standings, DateTimeOffset now)
    {
        Finish(standings, now);
        Version++;
    }

    /// <summary>
    /// The server restarted (STORY-028, DEC-030): the player whose turn it was gets a full new turn from
    /// <paramref name="now"/>, without a missed turn, since the downtime wasn't theirs. Returns whether anything changed;
    /// only an active match has a turn to restore.
    /// </summary>
    public bool ResumeAfterRestart(DateTimeOffset now)
    {
        if (Status != RoomStatus.Active)
        {
            return false;
        }

        TurnEndsAt = now + TurnTimeout!.Value;
        Version++;
        return true;
    }

    /// <summary>
    /// Times out the overdue turn (ENG-007, DEC-027): <paramref name="state"/> is the game's safe default for it, or null
    /// when the turn is just skipped. The current player has now missed one more turn in a row; at
    /// <paramref name="maxMissedTurns"/> they forfeit, otherwise the next player's turn begins.
    /// </summary>
    /// <returns>Whether the current player forfeited.</returns>
    public bool TimeOutTurn(string? state, int maxMissedTurns, DateTimeOffset now)
    {
        if (!IsTurnOverdue(now))
        {
            throw new InvalidOperationException("Only an overdue turn of an active match can time out.");
        }

        if (state is not null)
        {
            State = state;
        }

        var current = CurrentMember();
        var forfeited = current.MissTurn() >= maxMissedTurns;
        if (forfeited)
        {
            Forfeit(current, now);
        }
        else
        {
            NextTurn(now);
        }

        Version++;
        return forfeited;
    }

    /// <summary>
    /// Why <paramref name="playerId"/> can't act on this match with <paramref name="command"/> now, or null if they can
    /// (STORY-024, DEC-026): only the current player acts, before their turn runs out (STORY-025, DEC-027). The caller has
    /// checked that the player is a member.
    /// </summary>
    public MatchCommandProblem? CheckCommand(Guid playerId, MatchCommand command, DateTimeOffset now)
    {
        if (command.MatchId != Id)
        {
            return MatchCommandProblem.WrongMatch;
        }

        if (Status == RoomStatus.Waiting)
        {
            return MatchCommandProblem.NotStarted;
        }

        if (Status != RoomStatus.Active)
        {
            return MatchCommandProblem.Closed;
        }

        if (!IsPlaying(playerId))
        {
            return MatchCommandProblem.NotPlaying;
        }

        if (command.ExpectedVersion != Version)
        {
            return MatchCommandProblem.StaleVersion;
        }

        if (playerId != CurrentPlayerId)
        {
            return MatchCommandProblem.NotYourTurn;
        }

        return IsTurnOverdue(now) ? MatchCommandProblem.TurnExpired : null;
    }

    /// <summary>
    /// Applies a command the game's rules accepted: stores their new <paramref name="state"/>, ends the turn when the
    /// rules say so, bumps the version and returns the record that makes a retry of the same action return this result
    /// (DEC-026, DEC-027). Acting resets the player's missed turns. A command that finished the match comes with the
    /// <paramref name="standings"/> its rules decided, and the match is completed with them (STORY-028, DEC-030).
    /// </summary>
    public MatchAction ApplyCommand(
        Guid playerId,
        MatchCommand command,
        string state,
        bool endsTurn,
        DateTimeOffset now,
        IReadOnlyCollection<MatchStanding>? standings = null)
    {
        if (CheckCommand(playerId, command, now) is { } problem)
        {
            throw new InvalidOperationException($"The command cannot be applied: {problem}.");
        }

        if (standings is not null)
        {
            CheckStandings(standings);
        }

        State = state;
        CurrentMember().Act();
        if (standings is not null)
        {
            Finish(standings, now);
        }
        else if (endsTurn)
        {
            NextTurn(now);
        }

        Version++;
        return new MatchAction(Id, command.ActionId, playerId, command.Type, command.Payload.GetRawText(), Version, now);
    }

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
            CurrentPlayerId = null;
            TurnEndsAt = null;
        }
    }

    /// <summary>Completes the match and ranks every member; nothing changes if the standings are refused.</summary>
    private void Finish(IReadOnlyCollection<MatchStanding> standings, DateTimeOffset now)
    {
        if (Status != RoomStatus.Active)
        {
            throw new InvalidOperationException($"A room cannot go from {Status} to {RoomStatus.Completed}.");
        }

        CheckStandings(standings);
        MoveTo(RoomStatus.Completed, now);
        foreach (var standing in standings)
        {
            _members.Single(m => m.PlayerId == standing.PlayerId).RankAs(standing.Rank);
        }

        // Those who left rank after everyone still playing; players who left at the same moment share a place.
        var left = _members.Where(m => m.HasLeft).ToList();
        foreach (var member in left)
        {
            member.RankAs(standings.Count + 1 + left.Count(other => other.LeftAt > member.LeftAt));
        }
    }

    /// <summary>
    /// Standings must rank every player still playing exactly once, with competition ranks: each rank is one more than
    /// the number of players ranked above it, so the best rank is 1 and ties skip the places they share (1, 1, 3).
    /// </summary>
    private void CheckStandings(IReadOnlyCollection<MatchStanding> standings)
    {
        var playing = _members.Where(m => !m.HasLeft).Select(m => m.PlayerId).ToHashSet();
        if (standings.Count != playing.Count || !standings.Select(s => s.PlayerId).ToHashSet().SetEquals(playing))
        {
            throw new InvalidOperationException("The standings must rank every player still playing exactly once.");
        }

        if (standings.Any(s => s.Rank != 1 + standings.Count(other => other.Rank < s.Rank)))
        {
            throw new InvalidOperationException("The standings must use competition ranks from 1.");
        }
    }

    private RoomMember CurrentMember() => _members.Single(m => m.PlayerId == CurrentPlayerId);

    /// <summary>The next player still playing after the current one, in seat order, starts a new turn.</summary>
    private void NextTurn(DateTimeOffset now)
    {
        var seat = CurrentMember().Seat;
        var playing = _members.Where(m => !m.HasLeft).OrderBy(m => m.Seat).ToList();
        BeginTurn(playing.FirstOrDefault(m => m.Seat > seat) ?? playing[0], now);
    }

    private void BeginTurn(RoomMember player, DateTimeOffset now)
    {
        CurrentPlayerId = player.PlayerId;
        TurnNumber++;
        TurnEndsAt = now + TurnTimeout!.Value;
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

public enum MatchCommandProblem
{
    /// <summary>The command names another match.</summary>
    WrongMatch,

    /// <summary>The room is still waiting.</summary>
    NotStarted,

    /// <summary>The match is over or the room was cancelled.</summary>
    Closed,

    /// <summary>The player left (forfeited) this match.</summary>
    NotPlaying,

    /// <summary>The command's expected version is not the current one.</summary>
    StaleVersion,

    /// <summary>Another player is playing the current turn.</summary>
    NotYourTurn,

    /// <summary>The player's turn ran out; it is about to be timed out.</summary>
    TurnExpired,
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

    /// <summary>The member's place in the turn order, from 1, given at the start (DEC-027); null before it.</summary>
    public int? Seat { get; private set; }

    /// <summary>Turns in a row that ran out on the member; acting resets it (DEC-027).</summary>
    public int MissedTurns { get; private set; }

    /// <summary>
    /// The member's final place, from 1 for the winners, set once when the match is completed (STORY-028, DEC-030);
    /// null in any other status, so an abandoned match has no winner.
    /// </summary>
    public int? Rank { get; private set; }

    /// <summary>Ranked first in a completed match; tied winners all win.</summary>
    public bool Won => Rank == 1;

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

    /// <summary>Returns whether anything changed.</summary>
    internal bool MarkReady(CharacterKind kind, string characterId)
    {
        if (ReadyCharacterKind == kind && string.Equals(ReadyCharacterId, characterId, StringComparison.Ordinal))
        {
            return false;
        }

        ReadyCharacterKind = kind;
        ReadyCharacterId = characterId;
        return true;
    }

    /// <summary>Returns whether anything changed.</summary>
    internal bool MarkNotReady()
    {
        if (ReadyCharacterKind is null)
        {
            return false;
        }

        ReadyCharacterKind = null;
        ReadyCharacterId = null;
        return true;
    }

    internal void LockCharacter(CharacterSnapshot character) => Character = character;

    internal void Forfeit(DateTimeOffset now) => LeftAt = now;

    internal void TakeSeat(int seat) => Seat = seat;

    /// <summary>Returns the turns now missed in a row.</summary>
    internal int MissTurn() => ++MissedTurns;

    internal void Act() => MissedTurns = 0;

    internal void RankAs(int rank) => Rank = rank;
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
