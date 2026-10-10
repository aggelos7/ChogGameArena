using System.Net.Http.Json;
using System.Text.Json;
using ChogGames.Core.Nfts;
using ChogGames.Core.Wallets;
using Microsoft.Extensions.DependencyInjection;

namespace ChogGames.Api.Tests.Nfts;

/// <summary>Reads the real collection with the committed configuration; see <see cref="MonadLiveFactAttribute"/>.</summary>
public class MonadLiveTests
{
    private static readonly string RpcUrl = Environment.GetEnvironmentVariable(MonadLiveFactAttribute.EnvironmentVariable) ?? string.Empty;

    /// <summary>Owner of a token read directly with eth_call, independent of the provider.</summary>
    private static async Task<WalletAddress> OwnerOfAsync(string contract, int tokenId)
    {
        using var http = new HttpClient();
        var request = new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "eth_call",
            @params = new object[] { new { to = contract, data = "0x6352211e" + tokenId.ToString("x64") }, "latest" },
        };
        using var response = await http.PostAsJsonAsync(RpcUrl, request);
        var result = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("result").GetString()!;
        Assert.True(WalletAddress.TryParse("0x" + result[^40..], out var owner));
        return owner;
    }

    [MonadLiveFact]
    public async Task Owner_of_a_real_token_sees_it_listed_and_verified()
    {
        await using var factory = new ApiFactory(ApiFactory.UnusedDatabase, new Dictionary<string, string?> { ["Nfts:Monad:RpcUrl"] = RpcUrl });
        var provider = factory.Services.GetRequiredService<INftProvider>();
        var options = factory.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<Infrastructure.Nfts.NftOptions>>().Value.Monad;
        var owner = await OwnerOfAsync(options.ContractAddress, 1);

        var owned = await provider.GetOwnedAsync(owner, CancellationToken.None);

        Assert.Contains(owned, nft => nft.TokenId == NftTokenId.From(1));
        Assert.NotNull(await provider.FindOwnedAsync(owner, NftTokenId.From(1), CancellationToken.None));
        Assert.Null(await provider.FindOwnedAsync(owner, NftTokenId.From(1970), CancellationToken.None));
    }

    [MonadLiveFact]
    public async Task Wallet_without_chogs_owns_nothing()
    {
        await using var factory = new ApiFactory(ApiFactory.UnusedDatabase, new Dictionary<string, string?> { ["Nfts:Monad:RpcUrl"] = RpcUrl });
        var provider = factory.Services.GetRequiredService<INftProvider>();
        Assert.True(WalletAddress.TryParse("0x000000000000000000000000000000000000c000", out var nobody));

        Assert.Empty(await provider.GetOwnedAsync(nobody, CancellationToken.None));
        Assert.Null(await provider.FindOwnedAsync(nobody, NftTokenId.From(1), CancellationToken.None));
    }
}
