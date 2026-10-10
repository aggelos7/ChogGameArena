using System.Numerics;
using ChogGames.Infrastructure.Nfts.Metadata;

namespace ChogGames.Infrastructure.Nfts.Cache;

/// <summary>
/// Stored state of one token of a collection (NFT-003, NFT-010): last known owner with its refresh time, and
/// normalized metadata with its status and refresh time. Stored ownership is never authoritative for match
/// admission; it only backs listing during a short outage.
/// </summary>
public sealed class ChogNft
{
    public const int MaxUriLength = IpfsUri.MaxLength;

    public long ChainId { get; private set; }

    public string ContractAddress { get; private set; } = string.Empty;

    public BigInteger TokenId { get; private set; }

    /// <summary>Null when the token is not known to belong to any wallet that was scanned.</summary>
    public string? OwnerAddress { get; private set; }

    public DateTimeOffset? OwnerRefreshedAt { get; private set; }

    public NftMetadataStatus MetadataStatus { get; private set; }

    public DateTimeOffset? MetadataRefreshedAt { get; private set; }

    public string? MetadataUri { get; private set; }

    public string? Name { get; private set; }

    public string? ImageUri { get; private set; }

    public List<ChogNftTrait> Traits { get; private set; } = [];

    /// <summary>PostgreSQL row version, so two requests cannot both write metadata for the same token.</summary>
    public uint Version { get; private set; }

    private ChogNft()
    {
    }

    /// <summary>Whether metadata should be read again at <paramref name="now"/>.</summary>
    public bool NeedsMetadata(DateTimeOffset now, NftCacheOptions cache) => MetadataStatus switch
    {
        NftMetadataStatus.Pending => true,
        NftMetadataStatus.Unavailable => MetadataRefreshedAt is null || now - MetadataRefreshedAt >= cache.MetadataRetryInterval,
        _ => MetadataRefreshedAt is null || now - MetadataRefreshedAt >= cache.MetadataRefreshInterval,
    };

    /// <summary>
    /// Records a metadata read. An outage keeps previously read metadata and only moves the retry time,
    /// so a working token never loses its image or traits because a gateway was down.
    /// </summary>
    public void ApplyMetadata(NftMetadata metadata, DateTimeOffset now)
    {
        MetadataRefreshedAt = now;
        if (metadata.Status == NftMetadataStatus.Unavailable)
        {
            if (MetadataStatus is NftMetadataStatus.Pending or NftMetadataStatus.Unavailable)
            {
                MetadataStatus = NftMetadataStatus.Unavailable;
            }

            return;
        }

        MetadataStatus = metadata.Status;
        MetadataUri = metadata.MetadataUri?.ToString();
        Name = metadata.Name;
        ImageUri = metadata.Image?.ToString();
        Traits.Clear();
        for (var i = 0; i < metadata.Traits.Count; i++)
        {
            Traits.Add(new ChogNftTrait(i, metadata.Traits[i].Type, metadata.Traits[i].Value));
        }
    }
}

/// <summary>One normalized trait of a <see cref="ChogNft"/>, in metadata order.</summary>
public sealed class ChogNftTrait
{
    public ChogNftTrait(int position, string traitType, string traitValue)
    {
        Position = position;
        TraitType = traitType;
        TraitValue = traitValue;
    }

    public long Id { get; private set; }

    public int Position { get; private set; }

    public string TraitType { get; private set; }

    public string TraitValue { get; private set; }
}

/// <summary>When a wallet's holdings in a collection were last read from the chain.</summary>
public sealed class NftOwnershipScan
{
    public long ChainId { get; private set; }

    public string ContractAddress { get; private set; } = string.Empty;

    public string WalletAddress { get; private set; } = string.Empty;

    public DateTimeOffset ScannedAt { get; private set; }
}
