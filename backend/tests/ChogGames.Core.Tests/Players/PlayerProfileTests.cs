using ChogGames.Core.Players;

namespace ChogGames.Core.Tests.Players;

public class PlayerProfileTests
{
    private static Player NewGuest()
    {
        PlayerDisplayName.TryCreate("Bob", out var name);
        return Player.CreateGuest(name!, DateTimeOffset.UtcNow);
    }

    [Fact]
    public void New_guests_start_with_the_classic_avatar_and_no_games()
    {
        var player = NewGuest();

        Assert.Equal("classic", player.AvatarId);
        Assert.Equal(0, player.GamesPlayed);
        Assert.Equal(0, player.Wins);
    }

    [Fact]
    public void Rename_and_change_avatar_only_accept_validated_values()
    {
        var player = NewGuest();
        PlayerDisplayName.TryCreate("Robert", out var name);
        DefaultAvatar.TryFind("ocean", out var avatar);

        player.Rename(name!);
        player.ChangeAvatar(avatar);

        Assert.Equal("Robert", player.DisplayName);
        Assert.Equal("ocean", player.AvatarId);
    }

    [Theory]
    [InlineData("classic", true)]
    [InlineData("gold", true)]
    [InlineData("Gold", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Avatars_are_found_by_exact_id(string? id, bool exists)
    {
        Assert.Equal(exists, DefaultAvatar.TryFind(id, out _));
    }

    [Fact]
    public void Avatar_ids_are_unique_and_fit_the_column()
    {
        Assert.Equal(DefaultAvatar.All.Count, DefaultAvatar.All.Select(a => a.Id).Distinct().Count());
        Assert.All(DefaultAvatar.All, a => Assert.InRange(a.Id.Length, 1, DefaultAvatar.MaxIdLength));
        Assert.Contains(DefaultAvatar.Classic, DefaultAvatar.All);
    }
}
