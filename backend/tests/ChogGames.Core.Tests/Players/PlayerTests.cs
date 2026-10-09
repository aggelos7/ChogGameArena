using ChogGames.Core.Players;

namespace ChogGames.Core.Tests.Players;

public class PlayerTests
{
    [Fact]
    public void Guests_get_a_new_server_generated_id()
    {
        PlayerDisplayName.TryCreate("Bob", out var name);
        var now = DateTimeOffset.UtcNow;

        var first = Player.CreateGuest(name!, now);
        var second = Player.CreateGuest(name!, now);

        Assert.NotEqual(Guid.Empty, first.Id);
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(PlayerKind.Guest, first.Kind);
        Assert.Equal("Bob", first.DisplayName);
        Assert.Equal(now, first.CreatedAt);
    }
}
