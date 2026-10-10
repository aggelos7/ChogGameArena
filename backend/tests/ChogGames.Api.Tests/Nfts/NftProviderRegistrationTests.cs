using ChogGames.Core.Nfts;
using ChogGames.Infrastructure.Nfts;
using ChogGames.Infrastructure.Nfts.Monad;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ChogGames.Api.Tests.Nfts;

public class NftProviderRegistrationTests
{
    [Fact]
    public async Task Default_configuration_reads_the_official_collection_on_monad_mainnet()
    {
        await using var factory = new ApiFactory(ApiFactory.UnusedDatabase);

        var options = factory.Services.GetRequiredService<IOptions<NftOptions>>().Value;

        Assert.IsType<MonadNftProvider>(factory.Services.GetRequiredService<INftProvider>());
        Assert.Equal(NftProviderKind.Monad, options.Provider);
        Assert.Equal("https://rpc.monad.xyz", options.Monad.RpcUrl);
        Assert.Equal(143, options.Monad.ChainId);
        Assert.Equal("0xc96d31f8626c6d03fae5dcd3d61e3fb9f4a73763", options.Monad.ContractAddress);
        Assert.Equal("0xcA11bde05977b3631167028862bE2a173976CA11", options.Monad.Multicall3Address);
        Assert.Equal((1, 1969), (options.Monad.FirstTokenId, options.Monad.LastTokenId));
    }

    [Fact]
    public async Task Without_a_provider_no_ownership_is_ever_reported()
    {
        await using var factory = new ApiFactory(ApiFactory.UnusedDatabase, new Dictionary<string, string?> { ["Nfts:Provider"] = "None" });

        var provider = factory.Services.GetRequiredService<INftProvider>();

        Assert.IsType<UnconfiguredNftProvider>(provider);
        await Assert.ThrowsAsync<NftProviderUnavailableException>(() =>
            provider.GetOwnedAsync(MockNftFixtures.ManyHolder, CancellationToken.None));
    }

    [Fact]
    public async Task Mock_provider_works_without_rpc()
    {
        await using var factory = new ApiFactory(ApiFactory.UnusedDatabase, new Dictionary<string, string?>
        {
            ["Nfts:Provider"] = "Mock",
            ["Nfts:Mock:OtherWallets"] = "One",
        });

        var provider = factory.Services.GetRequiredService<INftProvider>();

        Assert.IsType<MockNftProvider>(provider);
        Assert.Same(provider, factory.Services.GetRequiredService<INftProvider>());
        Assert.Equal(4, (await provider.GetOwnedAsync(MockNftFixtures.ManyHolder, CancellationToken.None)).Count);
        Assert.Equal(MockNftHolding.One, factory.Services.GetRequiredService<IOptions<NftOptions>>().Value.Mock.OtherWallets);
    }

    [Fact]
    public async Task Mock_provider_stops_production_startup()
    {
        await using var factory = new ApiFactory(ApiFactory.UnusedDatabase, new Dictionary<string, string?> { ["Nfts:Provider"] = "Mock" });
        await using var production = factory.WithWebHostBuilder(builder => builder.UseEnvironment("Production"));

        var error = Assert.Throws<OptionsValidationException>(() => production.CreateClient());
        Assert.Contains("'Nfts:Provider' 'Mock' reports fake ownership and is not allowed in Production.", error.Message);
    }

    [Fact]
    public async Task Production_starts_with_the_monad_provider()
    {
        await using var factory = new ApiFactory(ApiFactory.UnusedDatabase);
        await using var production = factory.WithWebHostBuilder(builder => builder.UseEnvironment("Production"));

        using var client = production.CreateClient();

        Assert.IsType<MonadNftProvider>(production.Services.GetRequiredService<INftProvider>());
    }

    [Fact]
    public async Task Invalid_monad_settings_stop_startup()
    {
        await using var factory = new ApiFactory(ApiFactory.UnusedDatabase, new Dictionary<string, string?>
        {
            ["Nfts:Monad:ContractAddress"] = "0xnot-an-address",
        });

        var error = Assert.Throws<OptionsValidationException>(() => factory.CreateClient());
        Assert.Contains("'Nfts:Monad:ContractAddress' must be a 0x-prefixed 20-byte address.", error.Message);
    }

    [Theory]
    [InlineData("Nfts:Provider", "Alchemy")]
    [InlineData("Nfts:Mock:OtherWallets", "Lots")]
    public async Task Unknown_values_stop_startup(string key, string value)
    {
        await using var factory = new ApiFactory(ApiFactory.UnusedDatabase, new Dictionary<string, string?> { [key] = value });

        var error = Assert.ThrowsAny<InvalidOperationException>(() => factory.CreateClient());
        Assert.Contains(key, error.ToString());
        Assert.Contains(value, error.ToString());
    }
}
