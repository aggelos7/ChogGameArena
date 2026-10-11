namespace ChogGames.Infrastructure.Nfts;

public enum NftProviderKind
{
    /// <summary>No ownership source: every NFT request fails safely as unavailable.</summary>
    None,

    /// <summary>Local fixtures for development and tests; never allowed in Production.</summary>
    Mock,

    /// <summary>Reads the official collection from a Monad JSON-RPC endpoint (<see cref="MonadNftOptions"/>).</summary>
    Monad,
}

public enum MockNftHolding
{
    None,
    One,
    Many,
}

/// <summary>NFT ownership settings (<c>Nfts</c>).</summary>
public sealed class NftOptions
{
    public const string SectionName = "Nfts";

    public NftProviderKind Provider { get; set; } = NftProviderKind.None;

    /// <summary>Character gallery and selection requests per player per minute; each may read the chain.</summary>
    public int RequestsPerMinutePerPlayer { get; set; } = 30;

    public MockNftOptions Mock { get; set; } = new();

    public MonadNftOptions Monad { get; set; } = new();

    public IpfsOptions Ipfs { get; set; } = new();

    public NftCacheOptions Cache { get; set; } = new();
}

/// <summary>Mock provider settings (<c>Nfts:Mock</c>).</summary>
public sealed class MockNftOptions
{
    /// <summary>
    /// What a wallet that is not one of the fixture wallets owns, so a developer's own signed-in wallet
    /// can browse mock NFTs. The same fixture tokens are reported for every such wallet.
    /// </summary>
    public MockNftHolding OtherWallets { get; set; } = MockNftHolding.Many;
}

/// <summary>
/// Monad provider settings (<c>Nfts:Monad</c>). The collection is configuration, never user input (NFT-001);
/// the official contract is recorded in DEC-001.
/// </summary>
public sealed class MonadNftOptions
{
    /// <summary>JSON-RPC endpoint. May contain a provider key, so keep keyed URLs in secrets; it is never logged.</summary>
    public string RpcUrl { get; set; } = string.Empty;

    /// <summary>EIP-155 chain id the endpoint must report before any answer is trusted.</summary>
    public long ChainId { get; set; }

    /// <summary>The official Chog ERC-721 contract.</summary>
    public string ContractAddress { get; set; } = string.Empty;

    /// <summary>Multicall3, used only to list a wallet's tokens in a few calls; single-token checks call the contract directly.</summary>
    public string Multicall3Address { get; set; } = string.Empty;

    /// <summary>Token ids are scanned from <see cref="FirstTokenId"/> to <see cref="LastTokenId"/> inclusive.</summary>
    public long FirstTokenId { get; set; } = 1;

    public long LastTokenId { get; set; }

    /// <summary>ownerOf calls per Multicall3 request.</summary>
    public int BatchSize { get; set; } = 500;

    /// <summary>How long a collection-wide owner scan is reused for listing. Never used for single-token checks.</summary>
    public TimeSpan OwnershipCacheDuration { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(10);
}

/// <summary>IPFS gateway settings (<c>Nfts:Ipfs</c>) for metadata and image URLs.</summary>
public sealed class IpfsOptions
{
    /// <summary>
    /// Gateway bases ending in <c>/ipfs/</c>, tried in order for metadata; the first also serves image URLs to
    /// browsers. A dedicated gateway with a key belongs in secrets.
    /// </summary>
    public List<string> Gateways { get; set; } = [];

    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(10);
}

/// <summary>Stored NFT ownership and metadata settings (<c>Nfts:Cache</c>), used with the Monad provider.</summary>
public sealed class NftCacheOptions
{
    /// <summary>
    /// While the chain cannot be read, a wallet's list may come from its last stored scan if it is at most this old
    /// (NFT-003). Never used for single-token checks.
    /// </summary>
    public TimeSpan OwnershipFallback { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Metadata that was read (or found invalid) is read again after this long.</summary>
    public TimeSpan MetadataRefreshInterval { get; set; } = TimeSpan.FromDays(7);

    /// <summary>Metadata that could not be read because of an outage is retried after this long.</summary>
    public TimeSpan MetadataRetryInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Time one request may spend fetching metadata; the rest is fetched by later requests.</summary>
    public TimeSpan MetadataFetchBudget { get; set; } = TimeSpan.FromSeconds(5);

    public int MaxConcurrentMetadataFetches { get; set; } = 4;
}
