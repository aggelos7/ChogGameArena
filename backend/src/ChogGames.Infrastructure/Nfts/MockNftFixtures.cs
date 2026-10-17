using System.Text;
using ChogGames.Core.Nfts;
using ChogGames.Core.Wallets;

namespace ChogGames.Infrastructure.Nfts;

/// <summary>
/// Deterministic local NFT data (STORY-014). Fixture wallets are made-up addresses nobody holds a key for;
/// tokens include the awkward cases real metadata produces: no image, unknown traits and no traits at all.
/// Images are inline SVG so nothing is fetched from the network.
/// </summary>
public static class MockNftFixtures
{
    /// <summary>Owns no NFTs.</summary>
    public static readonly WalletAddress NoneHolder = Address("0x000000000000000000000000000000000000c000");

    /// <summary>Owns exactly one NFT.</summary>
    public static readonly WalletAddress OneHolder = Address("0x000000000000000000000000000000000000c001");

    /// <summary>Owns several NFTs, including the edge cases.</summary>
    public static readonly WalletAddress ManyHolder = Address("0x000000000000000000000000000000000000c002");

    /// <summary>Every request fails as if the RPC provider were down.</summary>
    public static readonly WalletAddress UnavailableHolder = Address("0x000000000000000000000000000000000000c003");

    /// <summary>A complete token.</summary>
    public static readonly OwnedNft Classic = new(
        NftTokenId.From(1),
        "Chog #1",
        Image(1, "#836ef9"),
        [
            new("Background", "Lavender"),
            new("Body", "Classic"),
            new("Eyes", "Happy"),
            new("Mouth", "Grin"),
            new("Head", "Beanie"),
            new("Accessory", "None"),
        ]);

    /// <summary>A complete token with a different look.</summary>
    public static readonly OwnedNft Golden = new(
        NftTokenId.From(7),
        "Chog #7",
        Image(7, "#f5c542"),
        [
            new("Background", "Sunset"),
            new("Body", "Golden"),
            new("Eyes", "Laser"),
            new("Mouth", "Smile"),
            new("Head", "Crown"),
            new("Accessory", "Chain"),
        ]);

    /// <summary>Metadata without a usable image.</summary>
    public static readonly OwnedNft MissingImage = new(
        NftTokenId.From(42),
        "Chog #42",
        null,
        [new("Background", "Mint"), new("Body", "Classic")]);

    /// <summary>Trait types and values the app has never seen, including non-ASCII and maximum-length text.</summary>
    public static readonly OwnedNft UnknownTraits = new(
        NftTokenId.From(108),
        "Chog #108",
        Image(108, "#3dd6b5"),
        [
            new("Mystery Aura", "Shimmering ✨"),
            new("Ετικέτα", "Χρυσό"),
            new("Season", "0"),
            new("Lore", new string('x', NftTrait.MaxLength)),
        ]);

    /// <summary>The highest token id of the collection, with no traits at all.</summary>
    public static readonly OwnedNft NoTraits = new(NftTokenId.From(1969), "Chog #1969", Image(1969, "#ff6b9a"), []);

    /// <summary>Every fixture token, whoever holds it.</summary>
    public static IReadOnlyList<OwnedNft> All => [Classic, Golden, MissingImage, UnknownTraits, NoTraits];

    public static IReadOnlyList<OwnedNft> Holding(MockNftHolding holding) => holding switch
    {
        MockNftHolding.None => [],
        MockNftHolding.One => [Classic],
        MockNftHolding.Many => [Golden, MissingImage, UnknownTraits, NoTraits],
        _ => throw new ArgumentOutOfRangeException(nameof(holding), holding, null),
    };

    private static WalletAddress Address(string value) =>
        WalletAddress.TryParse(value, out var address) ? address : throw new FormatException(value);

    private static Uri Image(int tokenId, string color)
    {
        var svg = $"""
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 120 120" role="img" aria-label="Mock Chog {tokenId}">
            <rect width="120" height="120" fill="#1b1435"/>
            <circle cx="60" cy="66" r="38" fill="{color}"/>
            <circle cx="47" cy="58" r="6" fill="#1b1435"/><circle cx="73" cy="58" r="6" fill="#1b1435"/>
            <text x="60" y="112" font-family="sans-serif" font-size="14" fill="#fff" text-anchor="middle">#{tokenId}</text>
            </svg>
            """;
        return new Uri("data:image/svg+xml;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(svg)));
    }
}
