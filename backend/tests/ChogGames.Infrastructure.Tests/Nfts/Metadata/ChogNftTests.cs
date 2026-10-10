using System.Reflection;
using ChogGames.Core.Nfts;
using ChogGames.Infrastructure.Nfts;
using ChogGames.Infrastructure.Nfts.Cache;
using ChogGames.Infrastructure.Nfts.Metadata;

namespace ChogGames.Infrastructure.Tests.Nfts.Metadata;

public class ChogNftTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
    private static readonly NftCacheOptions Cache = new();

    private static ChogNft NewRow() => (ChogNft)Activator.CreateInstance(typeof(ChogNft), nonPublic: true)!;

    private static IpfsUri Uri(string value)
    {
        Assert.True(IpfsUri.TryParse(value, out var uri));
        return uri;
    }

    private static NftMetadata Good(string name) => new(
        NftMetadataStatus.Ok,
        Uri("ipfs://bafybeid4ybujnscdipkno7ps5utqelp5ry5ryreqhay3zhgk3a6x4jruqu/1.json"),
        name,
        Uri("ipfs://bafybeihau2egcccbdxymeckmmbrqytbfor5gkt3en6d5fsbueymdbq7g6e/1.webp"),
        [new NftTrait("Tier", "Rare"), new NftTrait("Mystery", "?")]);

    [Fact]
    public void New_rows_need_metadata()
    {
        Assert.True(NewRow().NeedsMetadata(Now, Cache));
    }

    [Fact]
    public void Read_metadata_is_refreshed_after_the_refresh_interval()
    {
        var row = NewRow();
        row.ApplyMetadata(Good("A"), Now);

        Assert.False(row.NeedsMetadata(Now + Cache.MetadataRefreshInterval - TimeSpan.FromSeconds(1), Cache));
        Assert.True(row.NeedsMetadata(Now + Cache.MetadataRefreshInterval, Cache));
    }

    [Fact]
    public void Invalid_metadata_waits_for_the_refresh_interval_too()
    {
        var row = NewRow();
        row.ApplyMetadata(NftMetadata.Invalid(), Now);

        Assert.Equal(NftMetadataStatus.Invalid, row.MetadataStatus);
        Assert.False(row.NeedsMetadata(Now + TimeSpan.FromHours(1), Cache));
    }

    [Fact]
    public void Outages_are_retried_after_the_retry_interval()
    {
        var row = NewRow();
        row.ApplyMetadata(NftMetadata.Unavailable(), Now);

        Assert.Equal(NftMetadataStatus.Unavailable, row.MetadataStatus);
        Assert.False(row.NeedsMetadata(Now + Cache.MetadataRetryInterval - TimeSpan.FromSeconds(1), Cache));
        Assert.True(row.NeedsMetadata(Now + Cache.MetadataRetryInterval, Cache));
    }

    [Fact]
    public void An_outage_keeps_metadata_that_was_already_read()
    {
        var row = NewRow();
        row.ApplyMetadata(Good("A"), Now);

        row.ApplyMetadata(NftMetadata.Unavailable(), Now + TimeSpan.FromDays(8));

        Assert.Equal(NftMetadataStatus.Ok, row.MetadataStatus);
        Assert.Equal("A", row.Name);
        Assert.Equal(2, row.Traits.Count);
        Assert.Equal(Now + TimeSpan.FromDays(8), row.MetadataRefreshedAt);
    }

    [Fact]
    public void A_new_read_replaces_name_image_and_traits_in_order()
    {
        var row = NewRow();
        row.ApplyMetadata(Good("A"), Now);

        row.ApplyMetadata(Good("B") with { Image = null, Traits = [new NftTrait("Only", "One")] }, Now);

        Assert.Equal(("B", (string?)null), (row.Name, row.ImageUri));
        Assert.Equal(["0:Only=One"], row.Traits.Select(t => $"{t.Position}:{t.TraitType}={t.TraitValue}"));
    }
}
