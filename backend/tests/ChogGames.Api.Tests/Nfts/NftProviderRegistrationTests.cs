using ChogGames.Core.Nfts;
using ChogGames.Infrastructure.Nfts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ChogGames.Api.Tests.Nfts;

public class NftProviderRegistrationTests
{
    [Fact]
    public async Task Without_configuration_no_ownership_is_ever_reported()
    {
        await using var factory = new ApiFactory(ApiFactory.UnusedDatabase);

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
    public async Task Production_starts_without_a_provider()
    {
        await using var factory = new ApiFactory(ApiFactory.UnusedDatabase);
        await using var production = factory.WithWebHostBuilder(builder => builder.UseEnvironment("Production"));

        using var client = production.CreateClient();

        Assert.IsType<UnconfiguredNftProvider>(production.Services.GetRequiredService<INftProvider>());
    }

    [Theory]
    [InlineData("Nfts:Provider", "Monad")]
    [InlineData("Nfts:Mock:OtherWallets", "Lots")]
    public async Task Unknown_values_stop_startup(string key, string value)
    {
        await using var factory = new ApiFactory(ApiFactory.UnusedDatabase, new Dictionary<string, string?> { [key] = value });

        var error = Assert.ThrowsAny<InvalidOperationException>(() => factory.CreateClient());
        Assert.Contains(key, error.ToString());
        Assert.Contains(value, error.ToString());
    }
}
