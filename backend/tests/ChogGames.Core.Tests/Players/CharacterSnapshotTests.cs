using ChogGames.Core.Nfts;
using ChogGames.Core.Players;
using ChogGames.Core.Wallets;

namespace ChogGames.Core.Tests.Players;

public class CharacterSnapshotTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);

    private static WalletAddress Wallet()
    {
        Assert.True(WalletAddress.TryParse("0x" + new string('a', 40), out var wallet));
        return wallet;
    }

    [Fact]
    public void Standard_snapshot_has_the_chog_and_no_wallet_or_traits()
    {
        Assert.True(DefaultAvatar.TryFind("ocean", out var ocean));

        var snapshot = CharacterSnapshot.Standard(ocean, Now);

        Assert.Equal((CharacterKind.Standard, "ocean", "Ocean", null, null, Now), (snapshot.Kind, snapshot.CharacterId, snapshot.Name, snapshot.ImageUrl, snapshot.WalletAddress, snapshot.VerifiedAt));
        Assert.Empty(snapshot.Traits);
    }

    [Fact]
    public void Nft_snapshot_copies_token_metadata_and_verified_wallet()
    {
        var nft = new OwnedNft(NftTokenId.From(1), "CHOG #1462", new Uri("https://gateway.example.test/ipfs/bafy/1462.webp"), [new("Eyes", "Laser")]);

        var snapshot = CharacterSnapshot.Nft(Wallet(), nft, Now);

        Assert.Equal((CharacterKind.Nft, "1", "CHOG #1462", "0x" + new string('a', 40), Now), (snapshot.Kind, snapshot.CharacterId, snapshot.Name, snapshot.WalletAddress, snapshot.VerifiedAt));
        Assert.Equal(nft.ImageUrl, snapshot.ImageUrl);
        Assert.Equal([new NftTrait("Eyes", "Laser")], snapshot.Traits);
    }

    [Fact]
    public void Changing_the_source_traits_later_does_not_change_the_snapshot()
    {
        var traits = new List<NftTrait> { new("Eyes", "Laser") };
        var snapshot = CharacterSnapshot.Nft(Wallet(), new OwnedNft(NftTokenId.From(7), "Chog #7", null, traits), Now);

        traits[0] = new NftTrait("Eyes", "Sleepy");
        traits.Add(new NftTrait("Head", "Crown"));

        Assert.Equal([new NftTrait("Eyes", "Laser")], snapshot.Traits);
    }

    [Fact]
    public void Snapshots_with_the_same_content_are_equal()
    {
        var nft = new OwnedNft(NftTokenId.From(7), "Chog #7", null, [new("Eyes", "Laser")]);
        var copy = nft with { Traits = [new("Eyes", "Laser")] };

        Assert.Equal(CharacterSnapshot.Nft(Wallet(), nft, Now), CharacterSnapshot.Nft(Wallet(), copy, Now));
        Assert.NotEqual(CharacterSnapshot.Nft(Wallet(), nft, Now), CharacterSnapshot.Nft(Wallet(), nft with { Traits = [] }, Now));
    }
}
