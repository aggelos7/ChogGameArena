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
}
