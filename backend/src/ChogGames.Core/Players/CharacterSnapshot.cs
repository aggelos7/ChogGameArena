using System.Collections.Immutable;
using ChogGames.Core.Nfts;
using ChogGames.Core.Wallets;

namespace ChogGames.Core.Players;

/// <summary>
/// The character a player uses for one match, fixed when the match starts (NFT-008/NFT-009, ADR-006). It is a copy:
/// later transfers, metadata refreshes or a new gallery choice never change it. The next match takes a new one.
/// </summary>
public sealed record CharacterSnapshot
{
    private CharacterSnapshot(
        CharacterKind kind,
        string characterId,
        string name,
        Uri? imageUrl,
        ImmutableArray<NftTrait> traits,
        string? walletAddress,
        DateTimeOffset verifiedAt)
    {
        Kind = kind;
        CharacterId = characterId;
        Name = name;
        ImageUrl = imageUrl;
        Traits = traits;
        WalletAddress = walletAddress;
        VerifiedAt = verifiedAt;
    }

    public CharacterKind Kind { get; }

    /// <summary>A <see cref="DefaultAvatar"/> id for standard Chogs, a canonical token id for NFTs.</summary>
    public string CharacterId { get; }

    public string Name { get; }

    public Uri? ImageUrl { get; }

    /// <summary>Normalized traits as they were at match start (NFT-010), in an immutable copy; empty for standard Chogs.</summary>
    public IReadOnlyList<NftTrait> Traits { get; }

    /// <summary>The wallet whose ownership was verified on chain; null for standard Chogs.</summary>
    public string? WalletAddress { get; }

    /// <summary>When the character was checked: ownership read live for NFTs.</summary>
    public DateTimeOffset VerifiedAt { get; }

    public static CharacterSnapshot Standard(DefaultAvatar character, DateTimeOffset now) =>
        new(CharacterKind.Standard, character.Id, character.Name, null, [], null, now);

    /// <summary>For an NFT the caller has just verified <paramref name="owner"/> owns, read live from the chain.</summary>
    public static CharacterSnapshot Nft(WalletAddress owner, OwnedNft nft, DateTimeOffset now) =>
        new(CharacterKind.Nft, nft.TokenId.ToString(), nft.Name, nft.ImageUrl, [.. nft.Traits], owner.Value, now);

    /// <summary>Rebuilds a snapshot exactly as it was stored with its match (STORY-023, DEC-025); never a new check.</summary>
    public static CharacterSnapshot Restore(
        CharacterKind kind,
        string characterId,
        string name,
        Uri? imageUrl,
        IEnumerable<NftTrait> traits,
        string? walletAddress,
        DateTimeOffset verifiedAt) =>
        new(kind, characterId, name, imageUrl, [.. traits], walletAddress, verifiedAt);

    public bool Equals(CharacterSnapshot? other) =>
        other is not null
        && (Kind, CharacterId, Name, ImageUrl, WalletAddress, VerifiedAt) == (other.Kind, other.CharacterId, other.Name, other.ImageUrl, other.WalletAddress, other.VerifiedAt)
        && Traits.SequenceEqual(other.Traits);

    public override int GetHashCode() => HashCode.Combine(Kind, CharacterId, WalletAddress, VerifiedAt);
}
