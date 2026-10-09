using ChogGames.Core.Players;
using ChogGames.Core.Wallets;

namespace ChogGames.Core.Tests.Players;

public class PlayerWalletTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

    private static WalletAddress Wallet(char digit = 'a')
    {
        Assert.True(WalletAddress.TryParse("0x" + new string(digit, 40), out var address));
        return address;
    }

    private static Player Guest()
    {
        Assert.True(PlayerDisplayName.TryCreate("Alice", out var name));
        return Player.CreateGuest(name, Now);
    }

    [Fact]
    public void Binding_a_wallet_upgrades_a_guest_and_keeps_its_identity()
    {
        var player = Guest();
        var id = player.Id;

        player.BindWallet(Wallet());

        Assert.Equal((id, "Alice", PlayerKind.Wallet, "0x" + new string('a', 40)), (player.Id, player.DisplayName, player.Kind, player.WalletAddress));
    }

    [Fact]
    public void A_player_can_only_be_bound_once()
    {
        var player = Guest();
        player.BindWallet(Wallet('a'));

        Assert.Throws<InvalidOperationException>(() => player.BindWallet(Wallet('b')));
        Assert.Equal("0x" + new string('a', 40), player.WalletAddress);
    }

    [Fact]
    public void A_new_wallet_player_starts_with_defaults()
    {
        Assert.True(PlayerDisplayName.TryCreate("Chog 1234", out var name));

        var player = Player.CreateForWallet(Wallet(), name, Now);

        Assert.Equal((PlayerKind.Wallet, "classic", 0, 0), (player.Kind, player.AvatarId, player.GamesPlayed, player.Wins));
        Assert.Equal("0x" + new string('a', 40), player.WalletAddress);
    }

    [Fact]
    public void Guests_have_no_wallet()
    {
        Assert.Null(Guest().WalletAddress);
    }
}
