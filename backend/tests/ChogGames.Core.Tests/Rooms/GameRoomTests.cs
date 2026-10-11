using ChogGames.Core.Games;
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
        room.Start(host.Id, [host, bob], Now);

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
        room.Start(host.Id, [host, bob], Now.AddMinutes(2));

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
        Assert.Throws<InvalidOperationException>(() => room.Start(bob.Id, [host, bob], Now));
        Assert.Equal(RoomStatus.Waiting, room.Status);
    }

    [Fact]
    public void A_ready_host_alone_cannot_start()
    {
        var host = Guest();
        var room = NewRoom(host);
        room.MarkReady(host);

        Assert.Equal(RoomStartProblem.NotEnoughPlayers, room.CheckStart(host.Id, [host]));
        Assert.Throws<InvalidOperationException>(() => room.Start(host.Id, [host], Now));
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
}
