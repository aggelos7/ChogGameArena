using ChogGames.Core.Nfts;

namespace ChogGames.Infrastructure.Nfts.Metadata;

public enum NftMetadataStatus
{
    /// <summary>Not fetched yet.</summary>
    Pending,

    /// <summary>Fetched and normalized.</summary>
    Ok,

    /// <summary>The token URI or document is unusable; retried only after the refresh interval.</summary>
    Invalid,

    /// <summary>RPC or gateway failure; retried after the retry interval.</summary>
    Unavailable,
}

/// <summary>Result of reading one token's metadata. Name and image are null when missing or unusable.</summary>
public sealed record NftMetadata(
    NftMetadataStatus Status,
    IpfsUri? MetadataUri,
    string? Name,
    IpfsUri? Image,
    IReadOnlyList<NftTrait> Traits)
{
    public static NftMetadata Unavailable(IpfsUri? metadataUri = null) => new(NftMetadataStatus.Unavailable, metadataUri, null, null, []);

    public static NftMetadata Invalid(IpfsUri? metadataUri = null) => new(NftMetadataStatus.Invalid, metadataUri, null, null, []);
}

/// <summary>Reads a token's metadata. Never throws for bad data or outages; only cancellation propagates.</summary>
public interface INftMetadataSource
{
    Task<NftMetadata> FetchAsync(NftTokenId tokenId, CancellationToken cancellationToken);
}

/// <summary>Reads <c>tokenURI(tokenId)</c> from the collection contract.</summary>
public interface ITokenUriReader
{
    /// <returns>The URI, or null when the token does not exist.</returns>
    /// <exception cref="NftProviderUnavailableException">The chain could not be read.</exception>
    Task<string?> GetTokenUriAsync(NftTokenId tokenId, CancellationToken cancellationToken);
}
