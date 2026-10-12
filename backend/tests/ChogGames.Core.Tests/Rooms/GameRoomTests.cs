using System.Text.Json;
using ChogGames.Core.Games;
using ChogGames.Core.Matches;
using ChogGames.Core.Players;
using ChogGames.Core.Rooms;

namespace ChogGames.Core.Tests.Rooms;

public class GameRoomTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);

    private static Player Guest()
    {
        Assert.True(PlayerDisplayName.TryCreate("Alice", out var name));
        return Player.CreateGuest(name, Now);
    }

    [Fact]
    public void New_room_is_waiting_with_the_host_as_its_only_member()
    {
        var host = Guest();
        Assert.True(RoomCode.TryParse("K7QF3M", out var code));

        var room = GameRoom.Create(GameDefinition.Create(GameDefinition.ChogLife, 2, 6), host, code, Now);

        Assert.NotEqual(Guid.Empty, room.Id);
        Assert.Equal(("K7QF3M", "chog-life", host.Id), (room.Code, room.GameType, room.HostPlayerId));
        Assert.Equal((RoomStatus.Waiting, 6, Now), (room.Status, room.Capacity, room.CreatedAt));
        var member = Assert.Single(room.Members);
        Assert.Equal((room.Id, host.Id, Now), (member.RoomId, member.PlayerId, member.JoinedAt));
    }

    [Fact]
    public void Capacity_comes_from_the_game_definition()
    {
        Assert.True(RoomCode.TryParse("K7QF3M", out var code));

        var room = GameRoom.Create(GameDefinition.Create("chog-duel", 2, 2), Guest(), code, Now);

        Assert.Equal(2, room.Capacity);
    }

    private static GameRoom NewRoom(Player host, int maxPlayers = 6)
    {
        Assert.True(RoomCode.TryParse("K7QF3M", out var code));
        return GameRoom.Create(GameDefinition.Create("chog-life", 2, maxPlayers), host, code, Now);
    }

    [Fact]
    public void Players_join_until_the_room_is_full()
    {
        var host = Guest();
        var room = NewRoom(host, maxPlayers: 3);
        var bob = Guest();
        var carol = Guest();

        room.Join(bob, Now.AddMinutes(1));
        room.Join(carol, Now.AddMinutes(2));

        Assert.Equal([host.Id, bob.Id, carol.Id], room.Members.Select(m => m.PlayerId));
        Assert.True(room.IsFull);
        Assert.True(room.IsMember(carol.Id));
        Assert.Throws<InvalidOperationException>(() => room.Join(Guest(), Now));
        Assert.Equal(3, room.Members.Count);
    }

    [Fact]
    public void A_member_cannot_join_twice()
    {
        var host = Guest();
        var room = NewRoom(host);

        Assert.Throws<InvalidOperationException>(() => room.Join(host, Now));
        Assert.Single(room.Members);
    }

    [Theory]
    [InlineData(1, 6)]
    [InlineData(2, 7)]
    [InlineData(4, 3)]
    public void Game_definition_rejects_player_limits_outside_two_to_six(int min, int max)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => GameDefinition.Create("chog-life", min, max));
    }

    [Theory]
    [InlineData("")]
    [InlineData("a-game-id-that-is-longer-than-32-chars")]
    public void Game_definition_rejects_empty_or_long_ids(string id)
    {
        Assert.Throws<ArgumentException>(() => GameDefinition.Create(id, 2, 6));
    }

    private static GameRoom RoomOf(Player host, params Player[] others)
    {
        var room = NewRoom(host);
        foreach (var other in others)
        {
            room.Join(other, Now);
        }

        return room;
    }

    private static Player Choosing(string chogId)
    {
        var player = Guest();
        Assert.True(DefaultAvatar.TryFind(chogId, out var chog));
        player.SelectStandardCharacter(chog);
        return player;
    }

    [Fact]
    public void Minimum_players_come_from_the_game_definition()
    {
        Assert.True(RoomCode.TryParse("K7QF3M", out var code));

        var room = GameRoom.Create(GameDefinition.Create("chog-trio", 3, 6), Guest(), code, Now);

        Assert.Equal(3, room.MinPlayers);
        Assert.Null(room.StartedAt);
    }

    [Fact]
    public void Ready_is_for_the_chog_selected_when_marking_ready()
    {
        var host = Choosing("mint");
        var room = NewRoom(host);
        var member = Assert.Single(room.Members);
        Assert.False(member.IsReadyWith(host));

        room.MarkReady(host);
        Assert.True(member.IsReadyWith(host));
        Assert.Equal((CharacterKind.Standard, "mint"), (member.ReadyCharacterKind, member.ReadyCharacterId));

        Assert.True(DefaultAvatar.TryFind("gold", out var gold));
        host.SelectStandardCharacter(gold);
        Assert.False(member.IsReadyWith(host));

        room.MarkReady(host);
        Assert.True(member.IsReadyWith(host));

        room.MarkNotReady(host.Id);
        Assert.False(member.IsReadyWith(host));
        Assert.Null(member.ReadyCharacterId);
    }

    [Fact]
    public void Ready_of_one_member_is_not_anyone_elses()
    {
        var host = Guest();
        var bob = Guest();
        var room = RoomOf(host, bob);

        room.MarkReady(bob);

        Assert.False(room.Members.Single(m => m.PlayerId == bob.Id).IsReadyWith(host));
        Assert.False(room.Members.Single(m => m.PlayerId == host.Id).IsReadyWith(host));
    }

    [Fact]
    public void Ready_cannot_change_for_non_members_or_outside_a_waiting_room()
    {
        var host = Guest();
        var bob = Guest();
        var room = RoomOf(host, bob);

        Assert.Throws<InvalidOperationException>(() => room.MarkReady(Guest()));
        Assert.Throws<InvalidOperationException>(() => room.MarkNotReady(Guid.NewGuid()));

        room.MarkReady(host);
        room.MarkReady(bob);
        room.Start(host.Id, [host, bob], Snapshots(host, bob), Now);

        Assert.Throws<InvalidOperationException>(() => room.MarkNotReady(bob.Id));
        Assert.Throws<InvalidOperationException>(() => room.MarkReady(bob));
    }

    [Fact]
    public void Host_starts_when_enough_members_are_all_ready()
    {
        var host = Guest();
        var bob = Guest();
        var room = RoomOf(host, bob);
        room.MarkReady(host);
        room.MarkReady(bob);

        Assert.Null(room.CheckStart(host.Id, [host, bob]));
        room.Start(host.Id, [host, bob], Snapshots(host, bob), Now.AddMinutes(2));

        Assert.Equal((RoomStatus.Active, Now.AddMinutes(2)), (room.Status, room.StartedAt));
        Assert.Equal(RoomStartProblem.NotWaiting, room.CheckStart(host.Id, [host, bob]));
    }

    [Fact]
    public void Six_ready_players_can_start()
    {
        var host = Guest();
        var others = Enumerable.Range(0, 5).Select(_ => Guest()).ToArray();
        var room = RoomOf(host, others);
        Player[] everyone = [host, .. others];
        foreach (var player in everyone)
        {
            room.MarkReady(player);
        }

        Assert.Null(room.CheckStart(host.Id, everyone));
    }

    [Fact]
    public void Only_the_host_can_start()
    {
        var host = Guest();
        var bob = Guest();
        var room = RoomOf(host, bob);
        room.MarkReady(host);
        room.MarkReady(bob);

        Assert.Equal(RoomStartProblem.NotHost, room.CheckStart(bob.Id, [host, bob]));
        Assert.Throws<InvalidOperationException>(() => room.Start(bob.Id, [host, bob], Snapshots(host, bob), Now));
        Assert.Equal(RoomStatus.Waiting, room.Status);
    }

    [Fact]
    public void A_ready_host_alone_cannot_start()
    {
        var host = Guest();
        var room = NewRoom(host);
        room.MarkReady(host);

        Assert.Equal(RoomStartProblem.NotEnoughPlayers, room.CheckStart(host.Id, [host]));
        Assert.Throws<InvalidOperationException>(() => room.Start(host.Id, [host], Snapshots(host), Now));
    }

    [Fact]
    public void Every_member_including_the_host_must_be_ready()
    {
        var host = Guest();
        var bob = Guest();
        var room = RoomOf(host, bob);

        room.MarkReady(bob);
        Assert.Equal(RoomStartProblem.PlayersNotReady, room.CheckStart(host.Id, [host, bob]));

        room.MarkNotReady(bob.Id);
        room.MarkReady(host);
        Assert.Equal(RoomStartProblem.PlayersNotReady, room.CheckStart(host.Id, [host, bob]));
    }

    [Fact]
    public void Changing_chog_after_marking_ready_blocks_the_start()
    {
        var host = Guest();
        var bob = Choosing("mint");
        var room = RoomOf(host, bob);
        room.MarkReady(host);
        room.MarkReady(bob);

        Assert.True(DefaultAvatar.TryFind("berry", out var berry));
        bob.SelectStandardCharacter(berry);

        Assert.Equal(RoomStartProblem.PlayersNotReady, room.CheckStart(host.Id, [host, bob]));
    }

    [Fact]
    public void A_member_whose_player_is_missing_counts_as_not_ready()
    {
        var host = Guest();
        var bob = Guest();
        var room = RoomOf(host, bob);
        room.MarkReady(host);
        room.MarkReady(bob);

        Assert.Equal(RoomStartProblem.PlayersNotReady, room.CheckStart(host.Id, [host]));
    }

    [Fact]
    public void A_member_who_leaves_frees_their_seat_and_others_stay_ready()
    {
        var host = Guest();
        var bob = Guest();
        var carol = Guest();
        var room = RoomOf(host, bob, carol);
        room.MarkReady(host);
        room.MarkReady(carol);

        room.Leave(bob.Id, Now);

        Assert.Equal(RoomStatus.Waiting, room.Status);
        Assert.False(room.IsMember(bob.Id));
        Assert.Equal([host.Id, carol.Id], room.Members.Select(m => m.PlayerId));
        Assert.Null(room.CheckStart(host.Id, [host, carol]));
    }

    [Fact]
    public void A_seat_freed_by_leaving_can_be_taken()
    {
        var host = Guest();
        var room = NewRoom(host, maxPlayers: 2);
        var bob = Guest();
        room.Join(bob, Now);
        Assert.True(room.IsFull);

        room.Leave(bob.Id, Now);
        room.Join(Guest(), Now);

        Assert.Equal(2, room.Members.Count);
    }

    [Fact]
    public void The_host_leaving_cancels_the_room_and_keeps_its_members_listed()
    {
        var host = Guest();
        var bob = Guest();
        var room = RoomOf(host, bob);

        room.Leave(host.Id, Now);

        Assert.Equal(RoomStatus.Cancelled, room.Status);
        Assert.Equal([host.Id, bob.Id], room.Members.Select(m => m.PlayerId));
        Assert.Throws<InvalidOperationException>(() => room.Join(Guest(), Now));
        Assert.Throws<InvalidOperationException>(() => room.Leave(bob.Id, Now));
    }

    [Fact]
    public void Only_members_can_leave()
    {
        var host = Guest();
        var room = RoomOf(host, Guest());

        Assert.Throws<InvalidOperationException>(() => room.Leave(Guid.NewGuid(), Now));
        Assert.Equal((RoomStatus.Waiting, 2), (room.Status, room.Members.Count));
    }

    private static IReadOnlyDictionary<Guid, CharacterSnapshot> Snapshots(params Player[] players) =>
        players.ToDictionary(p => p.Id, p => CharacterSnapshot.Standard(DefaultAvatar.Classic, Now));

    /// <summary>A started match of <paramref name="players"/>, the first hosting.</summary>
    private static GameRoom Started(params Player[] players)
    {
        var room = RoomOf(players[0], players[1..]);
        foreach (var player in players)
        {
            room.MarkReady(player);
        }

        room.Start(players[0].Id, players, Snapshots(players), Now.AddMinutes(1));
        return room;
    }

    public static TheoryData<RoomStatus, RoomStatus> AllowedTransitions => new()
    {
        { RoomStatus.Waiting, RoomStatus.Active },
        { RoomStatus.Waiting, RoomStatus.Cancelled },
        { RoomStatus.Active, RoomStatus.Completed },
        { RoomStatus.Active, RoomStatus.Abandoned },
    };

    [Fact]
    public void Lifecycle_allows_exactly_the_documented_transitions()
    {
        var allowed = AllowedTransitions.Select(row => ((RoomStatus)row[0], (RoomStatus)row[1])).ToHashSet();
        foreach (var from in Enum.GetValues<RoomStatus>())
        {
            foreach (var to in Enum.GetValues<RoomStatus>())
            {
                Assert.Equal(allowed.Contains((from, to)), RoomLifecycle.CanMove(from, to));
            }
        }
    }

    [Theory]
    [InlineData(RoomStatus.Waiting, false)]
    [InlineData(RoomStatus.Active, false)]
    [InlineData(RoomStatus.Completed, true)]
    [InlineData(RoomStatus.Cancelled, true)]
    [InlineData(RoomStatus.Abandoned, true)]
    public void Completed_cancelled_and_abandoned_are_final(RoomStatus status, bool final)
    {
        Assert.Equal(final, RoomLifecycle.IsFinal(status));
        Assert.Equal(final, Enum.GetValues<RoomStatus>().All(to => !RoomLifecycle.CanMove(status, to)));
    }

    [Fact]
    public void Starting_locks_each_members_character_snapshot()
    {
        var host = Guest();
        var bob = Choosing("mint");
        var room = RoomOf(host, bob);
        room.MarkReady(host);
        room.MarkReady(bob);
        Assert.True(DefaultAvatar.TryFind("mint", out var mint));
        var characters = new Dictionary<Guid, CharacterSnapshot>
        {
            [host.Id] = CharacterSnapshot.Standard(DefaultAvatar.Classic, Now),
            [bob.Id] = CharacterSnapshot.Standard(mint, Now),
        };

        room.Start(host.Id, [host, bob], characters, Now.AddMinutes(1));

        Assert.Equal((RoomStatus.Active, Now.AddMinutes(1), (DateTimeOffset?)null), (room.Status, room.StartedAt, room.EndedAt));
        Assert.Equal(characters[host.Id], room.Members.Single(m => m.PlayerId == host.Id).Character);
        Assert.Equal("mint", room.Members.Single(m => m.PlayerId == bob.Id).Character!.CharacterId);
    }

    [Fact]
    public void A_start_without_every_snapshot_is_refused_unchanged()
    {
        var host = Guest();
        var bob = Guest();
        var room = RoomOf(host, bob);
        room.MarkReady(host);
        room.MarkReady(bob);

        Assert.Throws<InvalidOperationException>(() => room.Start(host.Id, [host, bob], Snapshots(host), Now));

        Assert.Equal((RoomStatus.Waiting, (DateTimeOffset?)null), (room.Status, room.StartedAt));
        Assert.All(room.Members, m => Assert.Null(m.Character));
    }

    [Fact]
    public void Cancelling_ends_the_room()
    {
        var host = Guest();
        var room = RoomOf(host, Guest());

        room.Leave(host.Id, Now.AddMinutes(3));

        Assert.Equal((RoomStatus.Cancelled, (DateTimeOffset?)null, Now.AddMinutes(3)), (room.Status, room.StartedAt, room.EndedAt));
        Assert.Throws<InvalidOperationException>(() => room.Complete(Now));
        Assert.Throws<InvalidOperationException>(() => room.Start(host.Id, [host], Snapshots(host), Now));
        Assert.Equal(RoomStatus.Cancelled, room.Status);
    }

    [Fact]
    public void Leaving_a_started_match_is_a_forfeit_and_the_match_goes_on()
    {
        var host = Guest();
        var bob = Guest();
        var carol = Guest();
        var room = Started(host, bob, carol);

        room.Leave(bob.Id, Now.AddMinutes(5));

        Assert.Equal((RoomStatus.Active, (DateTimeOffset?)null), (room.Status, room.EndedAt));
        Assert.Equal(3, room.Members.Count);
        var left = room.Members.Single(m => m.PlayerId == bob.Id);
        Assert.Equal((true, Now.AddMinutes(5)), (left.HasLeft, left.LeftAt));
        Assert.NotNull(left.Character);
        Assert.True(room.IsMember(bob.Id));
        Assert.True(room.HasLeft(bob.Id));
        Assert.False(room.IsPlaying(bob.Id));
        Assert.True(room.IsPlaying(carol.Id));
    }

    [Fact]
    public void The_host_leaving_a_started_match_does_not_end_it()
    {
        var host = Guest();
        var bob = Guest();
        var carol = Guest();
        var room = Started(host, bob, carol);

        room.Leave(host.Id, Now.AddMinutes(5));

        Assert.Equal(RoomStatus.Active, room.Status);
        Assert.True(room.HasLeft(host.Id));
    }

    [Fact]
    public void A_match_with_fewer_than_two_players_left_is_abandoned()
    {
        var host = Guest();
        var bob = Guest();
        var carol = Guest();
        var room = Started(host, bob, carol);

        room.Leave(carol.Id, Now.AddMinutes(4));
        room.Leave(host.Id, Now.AddMinutes(6));

        Assert.Equal((RoomStatus.Abandoned, Now.AddMinutes(1), Now.AddMinutes(6)), (room.Status, room.StartedAt, room.EndedAt));
        Assert.True(room.IsPlaying(bob.Id));
        Assert.Throws<InvalidOperationException>(() => room.Leave(bob.Id, Now));
        Assert.Throws<InvalidOperationException>(() => room.Complete(Now));
        Assert.Equal(RoomStatus.Abandoned, room.Status);
    }

    [Fact]
    public void A_player_cannot_leave_a_match_twice_or_one_they_are_not_in()
    {
        var host = Guest();
        var bob = Guest();
        var carol = Guest();
        var room = Started(host, bob, carol);
        room.Leave(bob.Id, Now.AddMinutes(2));

        Assert.Throws<InvalidOperationException>(() => room.Leave(bob.Id, Now.AddMinutes(3)));
        Assert.Throws<InvalidOperationException>(() => room.Leave(Guid.NewGuid(), Now));
        Assert.Equal(Now.AddMinutes(2), room.Members.Single(m => m.PlayerId == bob.Id).LeftAt);
        Assert.Equal(RoomStatus.Active, room.Status);
    }

    [Fact]
    public void A_started_match_is_closed_to_joins_ready_changes_and_starts()
    {
        var host = Guest();
        var bob = Guest();
        var room = Started(host, bob);

        Assert.Throws<InvalidOperationException>(() => room.Join(Guest(), Now));
        Assert.Throws<InvalidOperationException>(() => room.MarkNotReady(bob.Id));
        Assert.Throws<InvalidOperationException>(() => room.Start(host.Id, [host, bob], Snapshots(host, bob), Now));
        Assert.Equal((RoomStatus.Active, 2), (room.Status, room.Members.Count));
    }

    [Fact]
    public void A_finished_match_is_completed_and_final()
    {
        var host = Guest();
        var bob = Guest();
        var room = Started(host, bob);

        room.Complete(Now.AddMinutes(15));

        Assert.Equal((RoomStatus.Completed, Now.AddMinutes(15)), (room.Status, room.EndedAt));
        Assert.Throws<InvalidOperationException>(() => room.Complete(Now));
        Assert.Throws<InvalidOperationException>(() => room.Leave(bob.Id, Now));
        Assert.False(room.HasLeft(bob.Id));
    }

    [Fact]
    public void Only_a_started_match_can_be_completed()
    {
        var room = RoomOf(Guest(), Guest());

        Assert.Throws<InvalidOperationException>(() => room.Complete(Now));
        Assert.Equal((RoomStatus.Waiting, (DateTimeOffset?)null), (room.Status, room.EndedAt));
    }

    private static readonly JsonElement NoPayload = JsonSerializer.SerializeToElement(new { });

    private static MatchCommand Command(GameRoom room, long? expectedVersion = null, Guid? matchId = null) =>
        new(Guid.NewGuid(), matchId ?? room.Id, expectedVersion ?? room.Version, "ROLL", NoPayload);

    [Fact]
    public void Every_room_change_bumps_the_version_once()
    {
        var host = Choosing("classic");
        var bob = Choosing("mint");
        var carol = Choosing("gold");
        var room = NewRoom(host);
        Assert.Equal(0, room.Version);

        room.Join(bob, Now);
        room.Join(carol, Now);
        Assert.Equal(2, room.Version);

        room.MarkReady(bob);
        room.MarkReady(host);
        room.MarkNotReady(bob.Id);
        room.MarkReady(bob);
        Assert.Equal(6, room.Version);

        room.Leave(carol.Id, Now);
        Assert.Equal(7, room.Version);

        room.Start(host.Id, [host, bob], Snapshots(host, bob), Now);
        Assert.Equal(8, room.Version);

        room.Complete(Now);
        Assert.Equal(9, room.Version);
    }

    [Fact]
    public void Repeating_a_ready_status_changes_nothing()
    {
        var host = Choosing("classic");
        var room = NewRoom(host);
        room.MarkReady(host);
        var version = room.Version;

        room.MarkReady(host);
        room.MarkNotReady(host.Id);
        room.MarkNotReady(host.Id);

        Assert.Equal(version + 1, room.Version);
    }

    [Fact]
    public void Leaving_a_started_match_bumps_the_version_so_older_commands_are_stale()
    {
        var (alice, bob, carol) = (Choosing("classic"), Choosing("mint"), Choosing("gold"));
        var room = Started(alice, bob, carol);
        var command = Command(room);

        room.Leave(carol.Id, Now);

        Assert.Equal(MatchCommandProblem.StaleVersion, room.CheckCommand(alice.Id, command));
        Assert.Null(room.CheckCommand(alice.Id, Command(room)));
    }

    [Fact]
    public void An_accepted_command_stores_the_state_bumps_the_version_and_records_the_action()
    {
        var (alice, bob) = (Choosing("classic"), Choosing("mint"));
        var room = Started(alice, bob);
        var version = room.Version;
        var command = Command(room) with { Payload = JsonSerializer.SerializeToElement(new { steps = 3 }) };

        var action = room.ApplyCommand(bob.Id, command, """{"count":1}""", Now);

        Assert.Equal(("""{"count":1}""", version + 1), (room.State, room.Version));
        Assert.Equal(
            (room.Id, command.ActionId, bob.Id, "ROLL", """{"steps":3}""", version + 1, Now),
            (action.RoomId, action.ActionId, action.PlayerId, action.Type, action.Payload, action.Version, action.CreatedAt));
    }

    [Fact]
    public void Commands_need_an_active_match_the_player_still_plays_and_the_current_version()
    {
        var (alice, bob, carol) = (Choosing("classic"), Choosing("mint"), Choosing("gold"));
        var waiting = RoomOf(alice, bob);
        Assert.Equal(MatchCommandProblem.NotStarted, waiting.CheckCommand(alice.Id, Command(waiting)));

        var room = Started(alice, bob, carol);
        Assert.Equal(MatchCommandProblem.WrongMatch, room.CheckCommand(alice.Id, Command(room, matchId: Guid.NewGuid())));
        Assert.Equal(MatchCommandProblem.StaleVersion, room.CheckCommand(alice.Id, Command(room, expectedVersion: room.Version - 1)));
        Assert.Equal(MatchCommandProblem.StaleVersion, room.CheckCommand(alice.Id, Command(room, expectedVersion: room.Version + 1)));

        room.Leave(carol.Id, Now);
        Assert.Equal(MatchCommandProblem.NotPlaying, room.CheckCommand(carol.Id, Command(room)));

        room.Complete(Now);
        Assert.Equal(MatchCommandProblem.Closed, room.CheckCommand(alice.Id, Command(room)));
    }

    [Fact]
    public void A_command_that_fails_its_checks_is_refused_unchanged()
    {
        var (alice, bob) = (Choosing("classic"), Choosing("mint"));
        var room = Started(alice, bob);
        var version = room.Version;

        Assert.Throws<InvalidOperationException>(() => room.ApplyCommand(alice.Id, Command(room, expectedVersion: 0), "{}", Now));
        Assert.Throws<InvalidOperationException>(() => room.ApplyCommand(Guid.NewGuid(), Command(room), "{}", Now));

        Assert.Equal((version, (string?)null), (room.Version, room.State));
    }

    [Theory]
    [InlineData("ROLL", true)]
    [InlineData("END_TURN", true)]
    [InlineData("PLAY_CARD2", true)]
    [InlineData("roll", false)]
    [InlineData("_ROLL", false)]
    [InlineData("2ROLL", false)]
    [InlineData("ROLL-FAST", false)]
    [InlineData("", false)]
    [InlineData("ABCDEFGHIJKLMNOPQRSTUVWXYZ123456", true)]
    [InlineData("ABCDEFGHIJKLMNOPQRSTUVWXYZ1234567", false)]
    public void Command_types_are_short_upper_case_names(string type, bool valid)
    {
        Assert.Equal(valid, MatchCommand.IsValidType(type));
    }
}
