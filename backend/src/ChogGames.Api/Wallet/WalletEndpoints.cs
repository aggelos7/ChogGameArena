using Microsoft.Extensions.Options;

namespace ChogGames.Api.Wallet;

public static class WalletEndpoints
{
    public static IServiceCollection AddWalletChain(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IValidateOptions<WalletChainOptions>, WalletChainOptionsValidator>();
        services.AddOptions<WalletChainOptions>()
            .Bind(configuration.GetSection(WalletChainOptions.SectionName))
            .ValidateOnStart();

        return services;
    }

    /// <summary>
    /// <c>GET /api/wallet/chain</c>: the supported network, so the web app can ask wallets to switch to it
    /// (or add it). The chain is server configuration and never taken from the client.
    /// </summary>
    public static IEndpointRouteBuilder MapWalletEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/wallet/chain", (IOptions<WalletChainOptions> options) => ToResponse(options.Value))
            .WithName("GetWalletChain");

        return endpoints;
    }

    private static WalletChainResponse ToResponse(WalletChainOptions chain) => new(
        chain.ChainId,
        chain.Name,
        new NativeCurrencyResponse(chain.CurrencyName, chain.CurrencySymbol, chain.CurrencyDecimals),
        [.. chain.RpcUrls],
        [.. chain.BlockExplorerUrls]);
}

public sealed record WalletChainResponse(
    long ChainId,
    string Name,
    NativeCurrencyResponse NativeCurrency,
    string[] RpcUrls,
    string[] BlockExplorerUrls);

public sealed record NativeCurrencyResponse(string Name, string Symbol, int Decimals);
