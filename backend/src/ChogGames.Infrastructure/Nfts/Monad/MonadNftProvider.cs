using ChogGames.Core.Nfts;
using ChogGames.Core.Wallets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ChogGames.Infrastructure.Nfts.Monad;

/// <summary>
/// Reads Chog ownership from Monad over JSON-RPC (STORY-015, DEC-001). The collection is not ERC-721
/// Enumerable and public RPCs limit log queries, so listing scans <c>ownerOf</c> over the configured token
/// range through Multicall3 and reuses that scan briefly. A single-token check always calls the Chog
/// contract directly at the latest block and never uses the scan (NFT-003, NFT-008).
/// Metadata (images, traits) is added by STORY-016; tokens are named after their id until then.
/// </summary>
public sealed class MonadNftProvider(
    IHttpClientFactory httpClients,
    IOptions<NftOptions> options,
    TimeProvider timeProvider,
    ILogger<MonadNftProvider> logger) : INftProvider, IDisposable
{
    private const string UnavailableMessage = "NFT ownership could not be read from Monad.";

    private readonly SemaphoreSlim scanLock = new(1, 1);
    private volatile OwnershipScan? lastScan;
    private volatile bool chainVerified;

    private MonadNftOptions Settings => options.Value.Monad;

    private WalletAddress Contract => Address(Settings.ContractAddress);

    public async Task<IReadOnlyList<OwnedNft>> GetOwnedAsync(WalletAddress owner, CancellationToken cancellationToken)
    {
        RequireVerified(owner);
        var scan = await GetScanAsync(cancellationToken);
        return scan.Owners[owner].Select(ToNft).ToList();
    }

    public async Task<OwnedNft?> FindOwnedAsync(WalletAddress owner, NftTokenId tokenId, CancellationToken cancellationToken)
    {
        RequireVerified(owner);
        if (tokenId.Value < Settings.FirstTokenId || tokenId.Value > Settings.LastTokenId)
        {
            return null;
        }

        return await ReadAsync(async rpc =>
        {
            byte[] result;
            try
            {
                result = await rpc.CallAsync(Contract, ChogContractAbi.EncodeOwnerOf(tokenId), cancellationToken);
            }
            catch (RpcRevertedException)
            {
                // ERC-721 ownerOf reverts for tokens that do not exist (unminted or burned).
                return null;
            }

            return ChogContractAbi.DecodeOwner(result) == owner ? ToNft(tokenId) : null;
        }, cancellationToken);
    }

    public void Dispose() => scanLock.Dispose();

    private async Task<OwnershipScan> GetScanAsync(CancellationToken cancellationToken)
    {
        if (lastScan is { } fresh && IsFresh(fresh))
        {
            return fresh;
        }

        // One scan at a time; callers that waited reuse the scan that just finished.
        await scanLock.WaitAsync(cancellationToken);
        try
        {
            if (lastScan is { } scanned && IsFresh(scanned))
            {
                return scanned;
            }

            var scan = await ReadAsync(rpc => ScanAsync(rpc, cancellationToken), cancellationToken);
            lastScan = scan;
            return scan;
        }
        finally
        {
            scanLock.Release();
        }
    }

    private bool IsFresh(OwnershipScan scan) =>
        timeProvider.GetUtcNow() - scan.ReadAt < Settings.OwnershipCacheDuration;

    private async Task<OwnershipScan> ScanAsync(MonadRpcClient rpc, CancellationToken cancellationToken)
    {
        var settings = Settings;
        var contract = Contract;
        var multicall = Address(settings.Multicall3Address);
        var owned = new List<(WalletAddress Owner, NftTokenId TokenId)>();

        for (var start = settings.FirstTokenId; start <= settings.LastTokenId; start += settings.BatchSize)
        {
            var end = Math.Min(start + settings.BatchSize - 1, settings.LastTokenId);
            var tokenIds = new List<NftTokenId>();
            for (var id = start; id <= end; id++)
            {
                tokenIds.Add(NftTokenId.From((ulong)id));
            }

            byte[] result;
            try
            {
                result = await rpc.CallAsync(multicall, ChogContractAbi.EncodeAggregate3OwnerOf(contract, tokenIds), cancellationToken);
            }
            catch (RpcRevertedException)
            {
                throw new RpcUnavailableException("Multicall3 reverted.");
            }

            var returns = ChogContractAbi.DecodeAggregate3(result, tokenIds.Count);
            for (var i = 0; i < tokenIds.Count; i++)
            {
                // A failed entry is a token that does not exist; it has no owner.
                if (returns[i] is { } returnData && ChogContractAbi.DecodeOwner(returnData) is { } holder)
                {
                    owned.Add((holder, tokenIds[i]));
                }
            }
        }

        return new OwnershipScan(
            timeProvider.GetUtcNow(),
            owned.OrderBy(entry => entry.TokenId).ToLookup(entry => entry.Owner, entry => entry.TokenId));
    }

    /// <summary>Verifies the RPC's chain once, then runs <paramref name="read"/>; every failure becomes unavailable.</summary>
    private async Task<T> ReadAsync<T>(Func<MonadRpcClient, Task<T>> read, CancellationToken cancellationToken)
    {
        var rpc = new MonadRpcClient(httpClients.CreateClient(MonadRpcClient.HttpClientName));
        try
        {
            if (!chainVerified)
            {
                var chainId = await rpc.GetChainIdAsync(cancellationToken);
                if (chainId != Settings.ChainId)
                {
                    logger.LogError("Monad RPC reports chain {ActualChainId}, expected {ExpectedChainId}; NFT ownership is unavailable.",
                        chainId, Settings.ChainId);
                    throw new NftProviderUnavailableException(UnavailableMessage);
                }

                chainVerified = true;
            }

            return await read(rpc);
        }
        catch (Exception error) when (error is RpcUnavailableException or RpcRevertedException or AbiDecodingException)
        {
            // Only our own messages are logged: they never contain the RPC URL.
            logger.LogWarning("NFT ownership read failed: {Reason}", error.Message);
            throw new NftProviderUnavailableException(UnavailableMessage);
        }
    }

    private static void RequireVerified(WalletAddress owner)
    {
        if (owner.Value is null)
        {
            throw new ArgumentException("A verified wallet address is required.", nameof(owner));
        }
    }

    private static WalletAddress Address(string value) =>
        WalletAddress.TryParse(value, out var address) ? address : throw new InvalidOperationException("Validated at startup.");

    private static OwnedNft ToNft(NftTokenId tokenId) => new(tokenId, $"Chog #{tokenId}", null, []);

    private sealed record OwnershipScan(DateTimeOffset ReadAt, ILookup<WalletAddress, NftTokenId> Owners);
}
