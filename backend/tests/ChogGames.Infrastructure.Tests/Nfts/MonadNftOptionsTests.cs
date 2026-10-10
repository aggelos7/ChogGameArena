using ChogGames.Infrastructure.Nfts;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;

namespace ChogGames.Infrastructure.Tests.Nfts;

public class MonadNftOptionsTests
{
    private static NftOptions Valid() => new()
    {
        Provider = NftProviderKind.Monad,
        Monad =
        {
            RpcUrl = "https://rpc.example.test/key/abc",
            ChainId = 143,
            ContractAddress = "0x00000000000000000000000000000000000c0c00",
            Multicall3Address = "0x00000000000000000000000000000000000ca11a",
            FirstTokenId = 1,
            LastTokenId = 1969,
        },
        Ipfs = { Gateways = ["https://gateway.example/ipfs/", "https://dedicated.example/key-abc/ipfs/"] },
    };

    private static string? Validate(NftOptions options) =>
        new NftOptionsValidator(new HostingEnvironment { EnvironmentName = Environments.Production })
            .Validate(null, options).FailureMessage;

    [Fact]
    public void Valid_settings_pass_in_production()
    {
        Assert.Null(Validate(Valid()));
    }

    [Fact]
    public void Monad_settings_are_ignored_by_other_providers()
    {
        Assert.Null(Validate(new NftOptions { Provider = NftProviderKind.None }));
    }

    public static TheoryData<string, Action<MonadNftOptions>> Invalid => new()
    {
        { "'Nfts:Monad:RpcUrl'", o => o.RpcUrl = "" },
        { "'Nfts:Monad:RpcUrl'", o => o.RpcUrl = "http://rpc.example.test" },
        { "'Nfts:Monad:RpcUrl'", o => o.RpcUrl = "https://user:secret-pass@rpc.example.test" },
        { "'Nfts:Monad:RpcUrl'", o => o.RpcUrl = "wss://rpc.example.test" },
        { "'Nfts:Monad:ChainId'", o => o.ChainId = 0 },
        { "'Nfts:Monad:ContractAddress'", o => o.ContractAddress = "" },
        { "'Nfts:Monad:ContractAddress'", o => o.ContractAddress = "0x1234" },
        { "'Nfts:Monad:Multicall3Address'", o => o.Multicall3Address = "multicall" },
        { "'Nfts:Monad:FirstTokenId'", o => o.FirstTokenId = -1 },
        { "'Nfts:Monad:FirstTokenId'", o => o.LastTokenId = 0 },
        { "'Nfts:Monad:FirstTokenId'", o => o.LastTokenId = NftOptionsValidator.MaxScannedTokens + 1 },
        { "'Nfts:Monad:BatchSize'", o => o.BatchSize = 0 },
        { "'Nfts:Monad:BatchSize'", o => o.BatchSize = 1001 },
        { "'Nfts:Monad:OwnershipCacheDuration'", o => o.OwnershipCacheDuration = TimeSpan.FromSeconds(-1) },
        { "'Nfts:Monad:OwnershipCacheDuration'", o => o.OwnershipCacheDuration = TimeSpan.FromMinutes(11) },
        { "'Nfts:Monad:RequestTimeout'", o => o.RequestTimeout = TimeSpan.Zero },
        { "'Nfts:Monad:RequestTimeout'", o => o.RequestTimeout = TimeSpan.FromMinutes(2) },
    };

    [Theory]
    [MemberData(nameof(Invalid))]
    public void Invalid_settings_are_refused_without_echoing_the_rpc_url(string setting, Action<MonadNftOptions> change)
    {
        var options = Valid();
        change(options.Monad);

        var failure = Validate(options);

        Assert.NotNull(failure);
        Assert.Contains(setting, failure);
        Assert.DoesNotContain("secret", failure);
        Assert.DoesNotContain("key/abc", failure);
    }

    [Fact]
    public void Loopback_http_is_allowed_for_a_local_node()
    {
        var options = Valid();
        options.Monad.RpcUrl = "http://127.0.0.1:8545";

        Assert.Null(Validate(options));
    }

    public static TheoryData<string, Action<NftOptions>> InvalidIpfsAndCache => new()
    {
        { "'Nfts:Ipfs:Gateways' must list", o => o.Ipfs.Gateways = [] },
        { "'Nfts:Ipfs:Gateways:1'", o => o.Ipfs.Gateways[1] = "http://gateway.example/ipfs/" },
        { "'Nfts:Ipfs:Gateways:0'", o => o.Ipfs.Gateways[0] = "https://gateway.example/" },
        { "'Nfts:Ipfs:Gateways:0'", o => o.Ipfs.Gateways[0] = "https://gateway.example/ipfs/?key=secret" },
        { "'Nfts:Ipfs:Gateways:0'", o => o.Ipfs.Gateways[0] = "https://user:secret@gateway.example/ipfs/" },
        { "'Nfts:Ipfs:RequestTimeout'", o => o.Ipfs.RequestTimeout = TimeSpan.Zero },
        { "'Nfts:Cache:OwnershipFallback'", o => o.Cache.OwnershipFallback = TimeSpan.FromHours(2) },
        { "'Nfts:Cache:OwnershipFallback'", o => o.Cache.OwnershipFallback = TimeSpan.FromSeconds(-1) },
        { "'Nfts:Cache:MetadataRefreshInterval'", o => o.Cache.MetadataRefreshInterval = TimeSpan.Zero },
        { "'Nfts:Cache:MetadataRetryInterval'", o => o.Cache.MetadataRetryInterval = TimeSpan.FromSeconds(1) },
        { "'Nfts:Cache:MetadataRetryInterval'", o => o.Cache.MetadataRetryInterval = TimeSpan.FromDays(8) },
        { "'Nfts:Cache:MetadataFetchBudget'", o => o.Cache.MetadataFetchBudget = TimeSpan.FromMinutes(1) },
        { "'Nfts:Cache:MaxConcurrentMetadataFetches'", o => o.Cache.MaxConcurrentMetadataFetches = 0 },
        { "'Nfts:Cache:MaxConcurrentMetadataFetches'", o => o.Cache.MaxConcurrentMetadataFetches = 17 },
    };

    [Theory]
    [MemberData(nameof(InvalidIpfsAndCache))]
    public void Invalid_gateway_and_cache_settings_are_refused_without_echoing_urls(string setting, Action<NftOptions> change)
    {
        var options = Valid();
        change(options);

        var failure = Validate(options);

        Assert.NotNull(failure);
        Assert.Contains(setting, failure);
        Assert.DoesNotContain("secret", failure);
        Assert.DoesNotContain("key-abc", failure);
    }

    [Fact]
    public void Gateway_and_cache_settings_are_ignored_by_other_providers()
    {
        Assert.Null(Validate(new NftOptions { Provider = NftProviderKind.None, Cache = { MaxConcurrentMetadataFetches = 0 } }));
    }
}
