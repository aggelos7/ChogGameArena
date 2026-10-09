using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace ChogGames.Api.Tests.Wallet;

public class WalletChainEndpointTests
{
    private sealed record CurrencyDto(string Name, string Symbol, int Decimals);

    private sealed record ChainDto(long ChainId, string Name, CurrencyDto NativeCurrency, string[] RpcUrls, string[] BlockExplorerUrls);

    private static async Task<ChainDto> GetChainAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/api/wallet/chain");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ChainDto>(new JsonSerializerOptions(JsonSerializerDefaults.Web)))!;
    }

    [Fact]
    public async Task Default_chain_is_monad_mainnet_and_needs_no_session()
    {
        await using var factory = new ApiFactory(ApiFactory.UnusedDatabase);
        using var client = factory.CreateClient();

        var chain = await GetChainAsync(client);

        Assert.Equal(143, chain.ChainId);
        Assert.Equal("Monad Mainnet", chain.Name);
        Assert.Equal(new CurrencyDto("Monad", "MON", 18), chain.NativeCurrency);
        Assert.Equal(["https://rpc.monad.xyz"], chain.RpcUrls);
        Assert.Equal(["https://monadvision.com"], chain.BlockExplorerUrls);
    }

    [Fact]
    public async Task Chain_comes_from_configuration_and_ignores_client_input()
    {
        await using var factory = new ApiFactory(ApiFactory.UnusedDatabase, new Dictionary<string, string?>
        {
            ["Wallet:Chain:ChainId"] = "10143",
            ["Wallet:Chain:Name"] = "Monad Testnet",
        });
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Chain-Id", "1");

        using var response = await client.GetAsync("/api/wallet/chain?chainId=1");
        var chain = await response.Content.ReadFromJsonAsync<ChainDto>(new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Equal(10143, chain!.ChainId);
        Assert.Equal("Monad Testnet", chain.Name);
    }

    [Theory]
    [InlineData("Wallet:Chain:ChainId", "0", "'Wallet:Chain:ChainId' must be a positive EIP-155 chain id")]
    [InlineData("Wallet:Chain:RpcUrls:0", "http://rpc.monad.xyz", "'Wallet:Chain:RpcUrls:0' must be a public https:// URL")]
    [InlineData("Wallet:Chain:RpcUrls:0", "https://rpc.example.com/?key=secret", "'Wallet:Chain:RpcUrls:0' must be a public https:// URL")]
    public async Task Invalid_chain_configuration_stops_startup(string key, string value, string expectedMessage)
    {
        await using var factory = new ApiFactory(ApiFactory.UnusedDatabase, new Dictionary<string, string?> { [key] = value });

        var error = Assert.Throws<OptionsValidationException>(() => factory.CreateClient());
        Assert.Contains(expectedMessage, error.Message);
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    public async Task Chain_is_read_only(string method)
    {
        await using var factory = new ApiFactory(ApiFactory.UnusedDatabase);
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), "/api/wallet/chain"));

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }
}
