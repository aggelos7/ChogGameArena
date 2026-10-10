namespace ChogGames.Infrastructure.Nfts;

public enum NftProviderKind
{
    /// <summary>No ownership source: every NFT request fails safely as unavailable.</summary>
    None,

    /// <summary>Local fixtures for development and tests; never allowed in Production.</summary>
    Mock,
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

    public MockNftOptions Mock { get; set; } = new();
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
