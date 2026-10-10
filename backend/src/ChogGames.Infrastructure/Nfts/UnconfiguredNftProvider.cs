using ChogGames.Core.Nfts;
using ChogGames.Core.Wallets;

namespace ChogGames.Infrastructure.Nfts;

/// <summary>Used when no provider is configured: ownership is never assumed, every request fails as unavailable.</summary>
public sealed class UnconfiguredNftProvider : INftProvider
{
    public Task<IReadOnlyList<OwnedNft>> GetOwnedAsync(WalletAddress owner, CancellationToken cancellationToken) =>
        Task.FromException<IReadOnlyList<OwnedNft>>(Unavailable());

    public Task<OwnedNft?> FindOwnedAsync(WalletAddress owner, NftTokenId tokenId, CancellationToken cancellationToken) =>
        Task.FromException<OwnedNft?>(Unavailable());

    private static NftProviderUnavailableException Unavailable() =>
        new($"No NFT provider is configured ('{NftOptions.SectionName}:Provider').");
}
