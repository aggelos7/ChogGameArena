namespace ChogGames.Core.Nfts;

/// <summary>
/// A Chog NFT of the configured official collection that a provider reports as owned by a wallet.
/// <see cref="ImageUrl"/> is null when the metadata has no usable image; clients show a fallback.
/// </summary>
public sealed record OwnedNft(NftTokenId TokenId, string Name, Uri? ImageUrl, IReadOnlyList<NftTrait> Traits);
