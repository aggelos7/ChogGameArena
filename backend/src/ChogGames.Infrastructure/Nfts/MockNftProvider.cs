using ChogGames.Core.Nfts;
using ChogGames.Core.Wallets;
using Microsoft.Extensions.Options;

namespace ChogGames.Infrastructure.Nfts;

/// <summary>
/// Offline <see cref="INftProvider"/> backed by <see cref="MockNftFixtures"/>, for local development without RPC.
/// Mock ownership is not exclusive: every "other" wallet reports the same fixture tokens.
/// </summary>
public sealed class MockNftProvider(IOptions<NftOptions> options) : INftProvider
{
    public Task<IReadOnlyList<OwnedNft>> GetOwnedAsync(WalletAddress owner, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Holdings(owner));
    }

    public Task<OwnedNft?> FindOwnedAsync(WalletAddress owner, NftTokenId tokenId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Holdings(owner).FirstOrDefault(nft => nft.TokenId == tokenId));
    }

    private IReadOnlyList<OwnedNft> Holdings(WalletAddress owner)
    {
        if (owner.Value is null)
        {
            throw new ArgumentException("A verified wallet address is required.", nameof(owner));
        }

        if (owner == MockNftFixtures.UnavailableHolder)
        {
            throw new NftProviderUnavailableException("Mock NFT provider: simulated ownership lookup failure.");
        }

        var holding = owner == MockNftFixtures.NoneHolder ? MockNftHolding.None
            : owner == MockNftFixtures.OneHolder ? MockNftHolding.One
            : owner == MockNftFixtures.ManyHolder ? MockNftHolding.Many
            : options.Value.Mock.OtherWallets;
        return MockNftFixtures.Holding(holding);
    }
}
