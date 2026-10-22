using System.Text.Json;
using ChogGames.Core.Games;
using ChogGames.Core.Matches;
using ChogGames.Core.Players;
using ChogGames.Core.Rooms;

namespace ChogGames.Core.Tests.Rooms;

/// <summary>Simultaneous rounds in the shared engine (STORY-070, DEC-081).</summary>
public class GameRoomRoundTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

    private static readonly MatchRoundTiming Timing = new(
        Decision: TimeSpan.FromSeconds(15), FirstOpensAfter: TimeSpan.FromSeconds(5), OpensAfter: TimeSpan.FromSeconds(20));

    private static readonly JsonElement NoPayload = JsonSerializer.SerializeToElement(new { });

    private static Player Guest()
    {
        Assert.True(PlayerDisplayName.TryCreate("Alice", out var name));
        var player = Player.CreateGuest(name, Now);
        player.SelectStandardCharacter(DefaultAvatar.Classic);
        return player;
    }

    /// <summary>A match in rounds of <paramref name="players"/>, the first hosting, seated in join order.</summary>
    private static GameRoom Started(params Player[] players)
    {
        Assert.True(RoomCode.TryParse("K7QF3M", out var code));
        var room = GameRoom.Create(GameDefinition.Create("chog-royale", 2, 6), players[0], code, Now);
        for (var i = 1; i < players.Length; i++)
        {
            room.Join(players[i], Now.AddSeconds(i));
        }

        foreach (var player in players)
        {
            room.MarkReady(player);
        }

        room.Start(
            players[0].Id,
            players,
            players.ToDictionary(p => p.Id, _ => CharacterSnapshot.Standard(DefaultAvatar.Classic, Now)),
            TimeSpan.FromSeconds(25),
            ScriptedRandomSource.KeepOrder,
            Now,
            startState: _ => "{}",
            timeLimit: TimeSpan.FromMinutes(5),
            rounds: Timing);
        return room;
    }

    private static DateTimeOffset Open => Now + Timing.FirstOpensAfter;

    private static MatchCommand Command(GameRoom room, long? expectedVersion = null) =>
        new(Guid.NewGuid(), room.Id, expectedVersion ?? room.Version, "SUBMIT", NoPayload);

    private static RoomMember Member(GameRoom room, Player player) => room.Members.Single(m => m.PlayerId == player.Id);

    [Fact]
    public void A_match_in_rounds_has_no_current_player_and_its_first_round_opens_after_the_first_pause()
    {
        var alice = Guest();
        var bob = Guest();

        var room = Started(alice, bob);

        Assert.Equal(MatchPlay.Rounds, room.Play);
        Assert.Null(room.CurrentPlayerId);
        Assert.Equal((1, Open, Open + Timing.Decision, Timing.Decision), (room.TurnNumber, room.RoundOpensAt, room.TurnEndsAt, room.TurnTimeout));
        Assert.Equal(room.Version, room.RoundVersion);
        Assert.Equal([1, 2], room.Members.Select(m => m.Seat!.Value));
        Assert.False(room.IsTurnOverdue(Open + Timing.Decision));
    }

    [Fact]
    public void Every_player_submits_in_the_same_round_without_making_the_others_stale()
    {
        var alice = Guest();
        var bob = Guest();
        var carol = Guest();
        var room = Started(alice, bob, carol);
        var roundVersion = room.Version;

        room.ApplySubmission(alice.Id, Command(room, roundVersion), "{\"a\":1}", locks: false, Open);
        Assert.Null(room.CheckCommand(bob.Id, Command(room, roundVersion), Open));
        room.ApplySubmission(bob.Id, Command(room, roundVersion), "{\"b\":1}", locks: false, Open);
        room.ApplySubmission(alice.Id, Command(room, roundVersion), "{\"a\":2}", locks: false, Open.AddSeconds(1));

        Assert.Equal(roundVersion + 3, room.Version);
        Assert.Equal([true, true, false], room.Members.Select(m => m.RoundSubmitted));
        Assert.False(room.IsRoundDue(Open.AddSeconds(1)));
    }

    [Fact]
    public void Commands_are_refused_before_the_round_opens_after_its_deadline_once_locked_and_when_built_on_an_earlier_round()
    {
        var alice = Guest();
        var bob = Guest();
        var room = Started(alice, bob);
        var before = room.Version - 1;

        Assert.Equal(MatchCommandProblem.RoundNotOpen, room.CheckCommand(alice.Id, Command(room), Now));
        Assert.Equal(MatchCommandProblem.RoundClosed, room.CheckCommand(alice.Id, Command(room), Open + Timing.Decision));
        Assert.Equal(MatchCommandProblem.StaleVersion, room.CheckCommand(alice.Id, Command(room, before), Open));
        Assert.Equal(MatchCommandProblem.StaleVersion, room.CheckCommand(alice.Id, Command(room, room.Version + 1), Open));

        room.ApplySubmission(alice.Id, Command(room), "{}", locks: true, Open);

        Assert.Equal(MatchCommandProblem.ActionLocked, room.CheckCommand(alice.Id, Command(room), Open));
        Assert.Throws<InvalidOperationException>(() => room.ApplySubmission(alice.Id, Command(room), "{}", locks: false, Open));
        Assert.True(Member(room, alice).RoundLocked);
    }

    [Fact]
    public void The_round_is_due_once_everyone_still_playing_has_locked_or_its_deadline_passed()
    {
        var alice = Guest();
        var bob = Guest();
        var room = Started(alice, bob);

        room.ApplySubmission(alice.Id, Command(room), "{}", locks: true, Open);
        Assert.False(room.IsRoundDue(Open));
        Assert.True(room.IsRoundDue(Open + Timing.Decision));

        room.ApplySubmission(bob.Id, Command(room), "{}", locks: true, Open.AddSeconds(2));
        Assert.True(room.IsRoundDue(Open.AddSeconds(2)));
    }

    [Fact]
    public void Resolving_starts_the_next_round_counts_missed_rounds_and_happens_once()
    {
        var alice = Guest();
        var bob = Guest();
        var room = Started(alice, bob);
        room.ApplySubmission(alice.Id, Command(room), "{}", locks: false, Open);
        var deadline = Open + Timing.Decision;
        var previousVersion = room.Version;

        room.ResolveRound("{\"round\":2}", deadline, Timing.OpensAfter);

        Assert.Equal((2, deadline + Timing.OpensAfter, deadline + Timing.OpensAfter + Timing.Decision), (room.TurnNumber, room.RoundOpensAt, room.TurnEndsAt));
        Assert.Equal((previousVersion + 1, (long?)previousVersion + 1), (room.Version, room.RoundVersion));
        Assert.Equal((0, 1), (Member(room, alice).MissedTurns, Member(room, bob).MissedTurns));
        Assert.All(room.Members, m => Assert.False(m.RoundSubmitted || m.RoundLocked));
        Assert.Equal("{\"round\":2}", room.State);
        Assert.Throws<InvalidOperationException>(() => room.ResolveRound("{}", deadline, Timing.OpensAfter));
        Assert.Equal(MatchCommandProblem.StaleVersion, room.CheckCommand(alice.Id, Command(room, previousVersion), room.RoundOpensAt!.Value));
    }

    [Fact]
    public void Missing_rounds_never_forfeits_a_player()
    {
        var alice = Guest();
        var bob = Guest();
        var room = Started(alice, bob);

        for (var round = 0; round < 5; round++)
        {
            room.ResolveRound("{}", room.TurnEndsAt!.Value, Timing.OpensAfter);
        }

        Assert.Equal(RoomStatus.Active, room.Status);
        Assert.All(room.Members, m => Assert.Equal((5, false), (m.MissedTurns, m.HasLeft)));
    }

    [Fact]
    public void A_round_whose_rules_give_standings_completes_the_match()
    {
        var alice = Guest();
        var bob = Guest();
        var room = Started(alice, bob);

        room.ResolveRound("{}", room.TurnEndsAt!.Value, Timing.OpensAfter, [new MatchStanding(bob.Id, 1), new MatchStanding(alice.Id, 2)]);

        Assert.Equal(RoomStatus.Completed, room.Status);
        Assert.Equal((2, 1), (Member(room, alice).Rank!.Value, Member(room, bob).Rank!.Value));
        Assert.Equal(((DateTimeOffset?)null, (long?)null, (DateTimeOffset?)null), (room.RoundOpensAt, room.RoundVersion, room.TurnEndsAt));
    }

    [Fact]
    public void A_player_leaving_when_everyone_else_has_locked_makes_the_round_due_now()
    {
        var alice = Guest();
        var bob = Guest();
        var carol = Guest();
        var room = Started(alice, bob, carol);
        room.ApplySubmission(alice.Id, Command(room), "{}", locks: true, Open);
        room.ApplySubmission(bob.Id, Command(room), "{}", locks: true, Open);

        room.Leave(carol.Id, Open.AddSeconds(3));

        Assert.True(room.IsRoundDue(Open.AddSeconds(3)));
        Assert.Equal(Open.AddSeconds(3), room.TurnEndsAt);
    }

    [Fact]
    public void A_leaver_with_others_still_deciding_keeps_the_deadline()
    {
        var alice = Guest();
        var bob = Guest();
        var carol = Guest();
        var room = Started(alice, bob, carol);

        room.Leave(carol.Id, Open);

        Assert.Equal(Open + Timing.Decision, room.TurnEndsAt);
        Assert.Equal(MatchCommandProblem.NotPlaying, room.CheckCommand(carol.Id, Command(room), Open));
    }

    [Fact]
    public void Turns_are_not_timed_out_in_rounds()
    {
        var room = Started(Guest(), Guest());

        Assert.Throws<InvalidOperationException>(() => room.TimeOutTurn(null, 3, Open + Timing.Decision));
    }

    [Fact]
    public void A_restart_gives_an_open_round_its_full_decision_window_and_leaves_one_not_yet_open()
    {
        var room = Started(Guest(), Guest());

        Assert.False(room.ResumeAfterRestart(Now.AddSeconds(1)));
        Assert.Equal(Open + Timing.Decision, room.TurnEndsAt);

        Assert.True(room.ResumeAfterRestart(Open.AddSeconds(10)));
        Assert.Equal(Open.AddSeconds(10) + Timing.Decision, room.TurnEndsAt);
        Assert.Equal(Open, room.RoundOpensAt);
    }

    [Fact]
    public void Round_timing_must_have_a_decision_window_and_no_negative_pause()
    {
        Assert.Throws<ArgumentException>(() => new MatchRoundTiming(TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero).Check());
        Assert.Throws<ArgumentException>(() => new MatchRoundTiming(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(-1), TimeSpan.Zero).Check());
        new MatchRoundTiming(TimeSpan.FromSeconds(1), TimeSpan.Zero, TimeSpan.Zero).Check();
    }

    [Fact]
    public void Turn_mode_submissions_are_refused()
    {
        var alice = Guest();
        var bob = Guest();
        Assert.True(RoomCode.TryParse("K7QF3M", out var code));
        var room = GameRoom.Create(GameDefinition.Create("chog-life", 2, 6), alice, code, Now);
        room.Join(bob, Now);
        room.MarkReady(alice);
        room.MarkReady(bob);
        room.Start(
            alice.Id,
            [alice, bob],
            new[] { alice, bob }.ToDictionary(p => p.Id, _ => CharacterSnapshot.Standard(DefaultAvatar.Classic, Now)),
            TimeSpan.FromSeconds(25),
            ScriptedRandomSource.KeepOrder,
            Now);

        Assert.Equal(MatchPlay.Turns, room.Play);
        Assert.Throws<InvalidOperationException>(() => room.ApplySubmission(alice.Id, Command(room), "{}", false, Now));
        Assert.False(room.IsRoundDue(Now.AddHours(1)));
        Assert.Null(room.RoundOpensAt);
    }
}
