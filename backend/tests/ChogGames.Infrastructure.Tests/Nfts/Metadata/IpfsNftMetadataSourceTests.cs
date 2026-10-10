using System.Net;
using System.Text;
using ChogGames.Core.Nfts;
using ChogGames.Infrastructure.Nfts;
using ChogGames.Infrastructure.Nfts.Metadata;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ChogGames.Infrastructure.Tests.Nfts.Metadata;

public class IpfsNftMetadataSourceTests
{
    private const string Cid = "bafybeid4ybujnscdipkno7ps5utqelp5ry5ryreqhay3zhgk3a6x4jruqu";
    private const string First = "https://first.example/ipfs/";
    private const string Second = "https://second.example/key-abc/ipfs/";
    private const string Document = """{"name":"CHOG #1462","image":"ipfs://bafybeihau2egcccbdxymeckmmbrqytbfor5gkt3en6d5fsbueymdbq7g6e/1462.webp","attributes":[{"trait_type":"Tier","value":"Uncommon"}]}""";

    private readonly FakeTokenUris tokenUris = new();
    private readonly FakeGateways gateways = new();

    private IpfsNftMetadataSource Source() => new(
        tokenUris,
        new GatewayClients(gateways),
        Options.Create(new NftOptions { Ipfs = { Gateways = [First, Second] } }),
        NullLogger<IpfsNftMetadataSource>.Instance);

    private Task<NftMetadata> Fetch(ulong tokenId = 1) => Source().FetchAsync(NftTokenId.From(tokenId), CancellationToken.None);

    [Fact]
    public async Task Reads_token_uri_then_the_document_through_the_first_gateway()
    {
        gateways.Respond = _ => Ok(Document);

        var metadata = await Fetch();

        Assert.Equal(NftMetadataStatus.Ok, metadata.Status);
        Assert.Equal("CHOG #1462", metadata.Name);
        Assert.Equal($"ipfs://{Cid}/1462.json", metadata.MetadataUri?.ToString());
        Assert.Equal([new Uri($"{First}{Cid}/1462.json")], gateways.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.MovedPermanently)]
    public async Task Falls_back_to_the_next_gateway(HttpStatusCode firstStatus)
    {
        gateways.Respond = url => url.Host == "first.example"
            ? new HttpResponseMessage(firstStatus) { Headers = { Location = new Uri("https://evil.example/") } }
            : Ok(Document);

        var metadata = await Fetch();

        Assert.Equal(NftMetadataStatus.Ok, metadata.Status);
        Assert.Equal(["first.example", "second.example"], gateways.Requests.Select(u => u.Host));
    }

    [Fact]
    public async Task All_gateways_failing_is_unavailable_not_invalid()
    {
        gateways.Respond = url => url.Host == "first.example"
            ? throw new HttpRequestException("refused")
            : throw new TaskCanceledException("timeout");

        var metadata = await Fetch();

        Assert.Equal(NftMetadataStatus.Unavailable, metadata.Status);
        Assert.Equal($"ipfs://{Cid}/1462.json", metadata.MetadataUri?.ToString());
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("[1,2,3]")]
    public async Task Bad_documents_are_invalid(string body)
    {
        gateways.Respond = _ => Ok(body);

        Assert.Equal(NftMetadataStatus.Invalid, (await Fetch()).Status);
    }

    [Fact]
    public async Task Oversized_documents_are_invalid_whatever_the_declared_length()
    {
        var big = $$"""{"name":"x","d":"{{new string('x', NftMetadataNormalizer.MaxDocumentBytes)}}"}""";
        gateways.Respond = _ => Ok(big);
        Assert.Equal(NftMetadataStatus.Invalid, (await Fetch()).Status);

        gateways.Respond = _ =>
        {
            var response = Ok(Document);
            response.Content.Headers.ContentLength = 10_000_000;
            return response;
        };
        Assert.Equal(NftMetadataStatus.Invalid, (await Fetch()).Status);
    }

    [Theory]
    [InlineData("https://evil.example/1.json")]
    [InlineData("http://169.254.169.254/latest/meta-data")]
    [InlineData("data:application/json,{}")]
    [InlineData("")]
    public async Task Non_ipfs_token_uris_are_never_requested(string tokenUri)
    {
        tokenUris.Uri = tokenUri;

        Assert.Equal(NftMetadataStatus.Invalid, (await Fetch()).Status);
        Assert.Empty(gateways.Requests);
    }

    [Fact]
    public async Task Nonexistent_token_is_invalid()
    {
        tokenUris.Uri = null;

        Assert.Equal(NftMetadataStatus.Invalid, (await Fetch()).Status);
        Assert.Empty(gateways.Requests);
    }

    [Fact]
    public async Task Rpc_outage_is_unavailable()
    {
        tokenUris.Fail = true;

        Assert.Equal(NftMetadataStatus.Unavailable, (await Fetch()).Status);
        Assert.Empty(gateways.Requests);
    }

    [Fact]
    public async Task Caller_cancellation_propagates()
    {
        gateways.Respond = _ => throw new TaskCanceledException();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Source().FetchAsync(NftTokenId.From(1), cancelled.Token));
    }

    private static HttpResponseMessage Ok(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class FakeTokenUris : ITokenUriReader
    {
        public string? Uri { get; set; } = $"ipfs://{Cid}/1462.json";

        public bool Fail { get; set; }

        public Task<string?> GetTokenUriAsync(NftTokenId tokenId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Fail ? throw new NftProviderUnavailableException("down") : Task.FromResult(Uri);
        }
    }

    private sealed class FakeGateways : HttpMessageHandler
    {
        public Func<Uri, HttpResponseMessage> Respond { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.NotFound);

        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(HttpMethod.Get, request.Method);
            Requests.Add(request.RequestUri!);
            return Task.FromResult(Respond(request.RequestUri!));
        }
    }

    private sealed class GatewayClients(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            Assert.Equal(IpfsNftMetadataSource.HttpClientName, name);
            return new HttpClient(handler, disposeHandler: false);
        }
    }
}
