using System.Net;
using ChogGames.Core.Nfts;
using ChogGames.Core.Wallets;
using ChogGames.Infrastructure.Nfts;
using ChogGames.Infrastructure.Nfts.Monad;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ChogGames.Infrastructure.Tests.Nfts.Monad;

public sealed class MonadNftProviderTests : IDisposable
{
    private const string RpcUrl = "https://rpc.example.test/secret-key-123/";

    private static readonly WalletAddress Contract = Address("0x00000000000000000000000000000000000c0c00");
    private static readonly WalletAddress Multicall = Address("0x00000000000000000000000000000000000ca11a");
    private static readonly WalletAddress Alice = Address("0x5aaeb6053f3e94c9b9a09f33669435e7ef1beaed");
    private static readonly WalletAddress Bob = Address("0xfb6916095ca1df60bb79ce92ce3ea74c37c5d359");
    private static readonly WalletAddress Carol = Address("0xdbf03b407c01e7cd3cbea99509d93f8dddc8c6fb");

    private readonly FakeMonadRpc rpc = new(Contract, Multicall);
    private readonly Clock clock = new();
    private readonly ListLogger<MonadNftProvider> logger = new();
    private readonly MonadNftProvider provider;

    public MonadNftProviderTests()
    {
        // Tokens 1-10: Alice owns 3, 5 and 9; Bob owns 1; Carol owns none; 10 does not exist (burned).
        foreach (var id in new[] { 2, 4, 6, 7, 8 })
        {
            rpc.Owners[id] = Address("0x00000000000000000000000000000000000000ee");
        }

        rpc.Owners[1] = Bob;
        rpc.Owners[3] = Alice;
        rpc.Owners[5] = Alice;
        rpc.Owners[9] = Alice;

        provider = new MonadNftProvider(
            new SingleClientFactory(rpc),
            Options.Create(new NftOptions
            {
                Provider = NftProviderKind.Monad,
                Monad =
                {
                    RpcUrl = RpcUrl,
                    ChainId = 143,
                    ContractAddress = Contract.Value,
                    Multicall3Address = Multicall.Value,
                    FirstTokenId = 1,
                    LastTokenId = 10,
                    BatchSize = 4,
                    OwnershipCacheDuration = TimeSpan.FromSeconds(30),
                },
            }),
            clock,
            logger);
    }

    public void Dispose() => provider.Dispose();

    private static WalletAddress Address(string value)
    {
        Assert.True(WalletAddress.TryParse(value, out var address));
        return address;
    }

    private async Task<string[]> Owned(WalletAddress owner) =>
        [.. (await provider.GetOwnedAsync(owner, CancellationToken.None)).Select(nft => nft.TokenId.ToString())];

    private Task<OwnedNft?> Find(WalletAddress owner, ulong tokenId) =>
        provider.FindOwnedAsync(owner, NftTokenId.From(tokenId), CancellationToken.None);

    [Fact]
    public async Task Wallets_with_zero_one_and_many_nfts_get_their_token_ids()
    {
        Assert.Equal(["3", "5", "9"], await Owned(Alice));
        Assert.Equal(["1"], await Owned(Bob));
        Assert.Empty(await Owned(Carol));
    }

    [Fact]
    public async Task Listing_scans_the_range_in_batches_and_reuses_the_scan_briefly()
    {
        await Owned(Alice);
        await Owned(Bob);

        // Tokens 1-10 in batches of 4: three Multicall3 calls, after one chain check.
        Assert.Equal(["chainId", "multicall", "multicall", "multicall"], rpc.Calls);

        clock.Advance(TimeSpan.FromSeconds(29));
        await Owned(Carol);
        Assert.Equal(3, rpc.Count("multicall"));
    }

    [Fact]
    public async Task Listing_sees_transfers_after_the_cache_expires()
    {
        Assert.Equal(["3", "5", "9"], await Owned(Alice));
        rpc.Owners[5] = Carol;

        Assert.Equal(["3", "5", "9"], await Owned(Alice));
        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(["3", "9"], await Owned(Alice));
        Assert.Equal(["5"], await Owned(Carol));
    }

    [Fact]
    public async Task Concurrent_listings_share_one_scan()
    {
        var gate = new TaskCompletionSource();
        rpc.Delay = gate.Task;
        var listings = Enumerable.Range(0, 5).Select(_ => Owned(Alice)).ToList();
        gate.SetResult();

        foreach (var listing in listings)
        {
            Assert.Equal(["3", "5", "9"], await listing);
        }

        Assert.Equal(3, rpc.Count("multicall"));
    }

    [Fact]
    public async Task Single_token_check_is_always_fresh_and_ignores_the_listing_cache()
    {
        await Owned(Alice);
        rpc.Owners[5] = Carol;

        Assert.Null(await Find(Alice, 5));
        Assert.Equal(NftTokenId.From(5), (await Find(Carol, 5))!.TokenId);
        Assert.Equal(2, rpc.Count("ownerOf"));
    }

    [Fact]
    public async Task Single_token_check_rejects_tokens_the_wallet_does_not_own()
    {
        var owned = await Find(Alice, 9);
        Assert.NotNull(owned);
        Assert.Equal((NftTokenId.From(9), "Chog #9", (Uri?)null, 0), (owned.TokenId, owned.Name, owned.ImageUrl, owned.Traits.Count));

        Assert.Null(await Find(Bob, 9));
        Assert.Null(await Find(Alice, 10));
    }

    [Theory]
    [InlineData(0UL)]
    [InlineData(11UL)]
    [InlineData(ulong.MaxValue)]
    public async Task Tokens_outside_the_collection_are_rejected_without_rpc(ulong tokenId)
    {
        Assert.Null(await Find(Alice, tokenId));
        Assert.Empty(rpc.Calls);
    }

    [Fact]
    public async Task Wrong_chain_is_never_trusted()
    {
        rpc.ChainId = 10143;

        await Assert.ThrowsAsync<NftProviderUnavailableException>(() => Owned(Alice));
        await Assert.ThrowsAsync<NftProviderUnavailableException>(() => Find(Alice, 3));
        Assert.Equal(["chainId", "chainId"], rpc.Calls);
    }

    [Fact]
    public async Task Chain_is_checked_once()
    {
        await Owned(Alice);
        await Find(Alice, 3);
        await Find(Alice, 5);

        Assert.Equal(1, rpc.Count("chainId"));
    }

    public static TheoryData<string, Func<HttpResponseMessage>> Failures => new()
    {
        { "HTTP 500", () => FakeMonadRpc.Json("{}", HttpStatusCode.InternalServerError) },
        { "HTTP 429", () => FakeMonadRpc.Json("{}", HttpStatusCode.TooManyRequests) },
        { "rate limit", () => FakeMonadRpc.Json("""{"jsonrpc":"2.0","id":1,"error":{"code":-32007,"message":"50/second request limit reached"}}""") },
        { "invalid JSON", () => FakeMonadRpc.Json("<html>bad gateway</html>") },
        { "no result", () => FakeMonadRpc.Json("""{"jsonrpc":"2.0","id":1}""") },
        { "wrong id", () => FakeMonadRpc.Json("""{"jsonrpc":"2.0","id":2,"result":"0x"}""") },
        { "number result", () => FakeMonadRpc.Json("""{"jsonrpc":"2.0","id":1,"result":143}""") },
        { "not hex", () => FakeMonadRpc.Json("""{"jsonrpc":"2.0","id":1,"result":"0xzz"}""") },
        { "odd hex", () => FakeMonadRpc.Json("""{"jsonrpc":"2.0","id":1,"result":"0x123"}""") },
        { "empty", () => FakeMonadRpc.Json("""{"jsonrpc":"2.0","id":1,"result":"0x"}""") },
        { "connection", () => throw new HttpRequestException("Connection refused (rpc.example.test:443/secret-key-123)") },
    };

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task Rpc_failures_become_a_controlled_error(string name, Func<HttpResponseMessage> failure)
    {
        // Let the chain check pass so each read hits the failure.
        await Find(Alice, 3);
        rpc.Override = _ => failure();

        var listing = await Assert.ThrowsAsync<NftProviderUnavailableException>(() => Owned(Alice));
        var check = await Assert.ThrowsAsync<NftProviderUnavailableException>(() => Find(Alice, 3));

        Assert.Equal("NFT ownership could not be read from Monad.", listing.Message);
        Assert.Null(listing.InnerException);
        Assert.Null(check.InnerException);
        Assert.NotEmpty(logger.Messages);
        Assert.All(logger.Messages, message => Assert.DoesNotContain("secret-key", message));
        Assert.False(string.IsNullOrEmpty(name));
    }

    [Fact]
    public async Task Failed_chain_check_is_a_controlled_error()
    {
        rpc.Override = _ => FakeMonadRpc.Json("""{"jsonrpc":"2.0","id":1,"result":"0x"}""");

        await Assert.ThrowsAsync<NftProviderUnavailableException>(() => Owned(Alice));
    }

    [Fact]
    public async Task Failed_scan_is_not_cached()
    {
        await Find(Alice, 3);
        rpc.Override = _ => FakeMonadRpc.Json("{}", HttpStatusCode.BadGateway);
        await Assert.ThrowsAsync<NftProviderUnavailableException>(() => Owned(Alice));

        rpc.Override = null;
        Assert.Equal(["3", "5", "9"], await Owned(Alice));
    }

    [Fact]
    public async Task Timeout_is_a_controlled_error()
    {
        rpc.Delay = Task.Delay(Timeout.Infinite);
        using var client = new HttpClient(rpc, disposeHandler: false) { BaseAddress = new Uri(RpcUrl), Timeout = TimeSpan.FromMilliseconds(100) };
        using var slow = new MonadNftProvider(new FixedClientFactory(client), Options.Create(new NftOptions
        {
            Monad = { RpcUrl = RpcUrl, ChainId = 143, ContractAddress = Contract.Value, Multicall3Address = Multicall.Value, LastTokenId = 10 },
        }), clock, logger);

        await Assert.ThrowsAsync<NftProviderUnavailableException>(() => slow.GetOwnedAsync(Alice, CancellationToken.None));
    }

    [Fact]
    public async Task Caller_cancellation_is_not_reported_as_an_outage()
    {
        rpc.Delay = Task.Delay(Timeout.Infinite);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.GetOwnedAsync(Alice, cancellation.Token));
    }

    [Fact]
    public async Task Malformed_owner_word_is_a_controlled_error()
    {
        await Find(Alice, 3);
        rpc.Override = _ => FakeMonadRpc.Json($$"""{"jsonrpc":"2.0","id":1,"result":"0x{{new string('f', 64)}}"}""");

        await Assert.ThrowsAsync<NftProviderUnavailableException>(() => Find(Alice, 3));
    }

    [Fact]
    public async Task Zero_address_owner_is_nobody()
    {
        rpc.Owners[3] = Address("0x0000000000000000000000000000000000000000");

        Assert.Null(await Find(Alice, 3));
        Assert.Equal(["5", "9"], await Owned(Alice));
        Assert.Empty(await Owned(Address("0x0000000000000000000000000000000000000000")));
    }

    [Fact]
    public async Task Unverified_default_address_is_rejected()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => provider.GetOwnedAsync(default, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => provider.FindOwnedAsync(default, NftTokenId.From(3), CancellationToken.None));
        Assert.Empty(rpc.Calls);
    }

    [Fact]
    public async Task Token_uri_is_read_from_the_contract()
    {
        Assert.Equal(
            "ipfs://bafybeid4ybujnscdipkno7ps5utqelp5ry5ryreqhay3zhgk3a6x4jruqu/3.json",
            await provider.GetTokenUriAsync(NftTokenId.From(3), CancellationToken.None));
        Assert.Null(await provider.GetTokenUriAsync(NftTokenId.From(10), CancellationToken.None));
        Assert.Null(await provider.GetTokenUriAsync(NftTokenId.From(11), CancellationToken.None));
        Assert.Equal(2, rpc.Count("tokenURI"));
    }

    [Fact]
    public async Task Token_uri_outage_is_a_controlled_error()
    {
        await Find(Alice, 3);
        rpc.Override = _ => FakeMonadRpc.Json("{}", HttpStatusCode.ServiceUnavailable);

        await Assert.ThrowsAsync<NftProviderUnavailableException>(() => provider.GetTokenUriAsync(NftTokenId.From(3), CancellationToken.None));
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 10, 10, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan by) => now += by;
    }

    private sealed class SingleClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            Assert.Equal(MonadRpcClient.HttpClientName, name);
            return new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri(RpcUrl) };
        }
    }

    private sealed class FixedClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception) + exception);
        }
    }
}
