using ChogGames.Core.Nfts;
using ChogGames.Core.Players;
using ChogGames.Core.Wallets;

namespace ChogGames.Core.Tests.Players;

public class PlayerCharacterTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);

    private static Player Guest()
    {
        Assert.True(PlayerDisplayName.TryCreate("Alice", out var name));
        return Player.CreateGuest(name, Now);
    }

    private static Player WalletPlayer()
    {
        Assert.True(PlayerDisplayName.TryCreate("Alice", out var name));
        Assert.True(WalletAddress.TryParse("0x" + new string('a', 40), out var wallet));
        return Player.CreateForWallet(wallet, name, Now);
    }

    [Fact]
    public void New_players_start_with_the_classic_standard_chog()
    {
        Assert.All([Guest(), WalletPlayer()], player =>
            Assert.Equal((CharacterKind.Standard, "classic"), (player.SelectedCharacterKind, player.SelectedCharacterId)));
    }

    [Fact]
    public void Any_player_can_select_a_standard_chog()
    {
        var player = Guest();
        Assert.True(DefaultAvatar.TryFind("ocean", out var ocean));

        player.SelectStandardCharacter(ocean);

        Assert.Equal((CharacterKind.Standard, "ocean"), (player.SelectedCharacterKind, player.SelectedCharacterId));
    }

    [Fact]
    public void A_wallet_player_can_select_an_nft_and_switch_back_to_standard()
    {
        var player = WalletPlayer();

        player.SelectNftCharacter(NftTokenId.From(1969));
        Assert.Equal((CharacterKind.Nft, "1969"), (player.SelectedCharacterKind, player.SelectedCharacterId));

        player.SelectStandardCharacter(DefaultAvatar.Classic);
        Assert.Equal((CharacterKind.Standard, "classic"), (player.SelectedCharacterKind, player.SelectedCharacterId));
    }

    [Fact]
    public void A_guest_cannot_select_an_nft()
    {
        var player = Guest();

        Assert.Throws<InvalidOperationException>(() => player.SelectNftCharacter(NftTokenId.From(1)));
        Assert.Equal((CharacterKind.Standard, "classic"), (player.SelectedCharacterKind, player.SelectedCharacterId));
    }
}
