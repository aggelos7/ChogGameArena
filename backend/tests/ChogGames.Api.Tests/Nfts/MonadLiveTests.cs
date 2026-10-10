using System.Net.Http.Json;
using System.Text.Json;
using ChogGames.Core.Nfts;
using ChogGames.Core.Wallets;
using Microsoft.Extensions.DependencyInjection;

namespace ChogGames.Api.Tests.Nfts;

/// <summary>Reads the real collection with the committed configuration; see <see cref="MonadLiveFactAttribute"/>.</summary>
public class MonadLiveTests(MigratedPostgresDatabase database) : IClassFixture<MigratedPostgresDatabase>
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
        var provider = factory.Services.GetRequiredService<Infrastructure.Nfts.Monad.MonadNftProvider>();
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
        var provider = factory.Services.GetRequiredService<Infrastructure.Nfts.Monad.MonadNftProvider>();
        Assert.True(WalletAddress.TryParse("0x000000000000000000000000000000000000c000", out var nobody));

        Assert.Empty(await provider.GetOwnedAsync(nobody, CancellationToken.None));
        Assert.Null(await provider.FindOwnedAsync(nobody, NftTokenId.From(1), CancellationToken.None));
    }

    [MonadLiveFact(requiresPostgres: true)]
    public async Task Real_metadata_is_read_through_the_gateway_and_stored()
    {
        await using var factory = new ApiFactory(database.ConnectionString, new Dictionary<string, string?>
        {
            ["Nfts:Monad:RpcUrl"] = RpcUrl,
            ["Nfts:Cache:MetadataFetchBudget"] = "00:00:30",
        });
        var options = factory.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<Infrastructure.Nfts.NftOptions>>().Value.Monad;
        var owner = await OwnerOfAsync(options.ContractAddress, 1);
        using var scope = factory.Services.CreateScope();
        var provider = scope.ServiceProvider.GetRequiredService<INftProvider>();

        var token = await provider.FindOwnedAsync(owner, NftTokenId.From(1), CancellationToken.None);

        // Token 1's metadata is file 1462 of the shuffled collection (read 2026-10-10).
        Assert.NotNull(token);
        Assert.Equal("CHOG #1462", token.Name);
        Assert.Equal(12, token.Traits.Count);
        Assert.Contains(token.Traits, t => t.Type == "Tier");
        Assert.Equal("https://gateway.pinata.cloud/ipfs/bafybeihau2egcccbdxymeckmmbrqytbfor5gkt3en6d5fsbueymdbq7g6e/1462.webp", token.ImageUrl?.ToString());
    }
}
