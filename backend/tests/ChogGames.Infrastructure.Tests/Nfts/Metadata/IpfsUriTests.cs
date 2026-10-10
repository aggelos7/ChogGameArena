using ChogGames.Infrastructure.Nfts.Metadata;

namespace ChogGames.Infrastructure.Tests.Nfts.Metadata;

public class IpfsUriTests
{
    private const string Cid = "bafybeid4ybujnscdipkno7ps5utqelp5ry5ryreqhay3zhgk3a6x4jruqu";

    [Theory]
    [InlineData("ipfs://" + Cid + "/1462.json", Cid, "1462.json")]
    [InlineData("ipfs://ipfs/" + Cid + "/1462.json", Cid, "1462.json")]
    [InlineData("IPFS://" + Cid, Cid, "")]
    [InlineData("ipfs://QmYwAPJzv5CZsnA625s3Xf2nemtYgPpHdWEz79ojWnPbdG/images/a_b-c.webp", "QmYwAPJzv5CZsnA625s3Xf2nemtYgPpHdWEz79ojWnPbdG", "images/a_b-c.webp")]
    public void Content_addressed_uris_are_accepted(string input, string cid, string path)
    {
        Assert.True(IpfsUri.TryParse(input, out var uri));
        Assert.Equal((cid, path), (uri.Cid, uri.Path));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("https://evil.example/1.json")]
    [InlineData("http://127.0.0.1/ipfs/" + Cid)]
    [InlineData("ipfs://")]
    [InlineData("ipfs://not-a-cid/1.json")]
    [InlineData("ipfs://" + Cid + "/../secret")]
    [InlineData("ipfs://" + Cid + "/a/./b")]
    [InlineData("ipfs://" + Cid + "//1.json")]
    [InlineData("ipfs://" + Cid + "/1.json?x=1")]
    [InlineData("ipfs://" + Cid + "/1.json#frag")]
    [InlineData("ipfs://" + Cid + "/%2e%2e/x")]
    [InlineData("ipfs://" + Cid + "/a b.json")]
    [InlineData("ipfs://" + Cid + "@evil.example/1.json")]
    [InlineData("data:application/json,{}")]
    public void Anything_else_is_rejected(string? input)
    {
        Assert.False(IpfsUri.TryParse(input, out _));
    }

    [Fact]
    public void Length_is_bounded()
    {
        Assert.False(IpfsUri.TryParse("ipfs://" + Cid + "/" + new string('a', IpfsUri.MaxLength), out _));
    }

    [Fact]
    public void Gateway_url_and_canonical_form()
    {
        Assert.True(IpfsUri.TryParse("ipfs://ipfs/" + Cid + "/1462.json", out var uri));

        Assert.Equal("ipfs://" + Cid + "/1462.json", uri.ToString());
        Assert.Equal(new Uri("https://gateway.example/ipfs/" + Cid + "/1462.json"), uri.ToGatewayUrl("https://gateway.example/ipfs/"));
    }
}
