using Microsoft.Extensions.Options;

namespace ChogGames.Api.Wallet;

public sealed class WalletChainOptionsValidator : IValidateOptions<WalletChainOptions>
{
    private const string Section = WalletChainOptions.SectionName;

    /// <summary>Largest integer a browser can represent exactly; wallets exchange chain ids as JS numbers/hex.</summary>
    private const long MaxChainId = 9_007_199_254_740_991;

    public ValidateOptionsResult Validate(string? name, WalletChainOptions options)
    {
        var errors = new List<string>();

        if (options.ChainId is <= 0 or > MaxChainId)
        {
            errors.Add($"'{Section}:ChainId' must be a positive EIP-155 chain id, got '{options.ChainId}'.");
        }

        if (string.IsNullOrWhiteSpace(options.Name))
        {
            errors.Add($"'{Section}:Name' is required.");
        }

        if (string.IsNullOrWhiteSpace(options.CurrencyName))
        {
            errors.Add($"'{Section}:CurrencyName' is required.");
        }

        // EIP-3085 limits: symbol 2-6 characters; wallets only accept 18 decimals for the native currency.
        if (options.CurrencySymbol.Length is < 2 or > 6 || options.CurrencySymbol.Any(char.IsWhiteSpace))
        {
            errors.Add($"'{Section}:CurrencySymbol' must be 2-6 characters without spaces, got '{options.CurrencySymbol}'.");
        }

        if (options.CurrencyDecimals != 18)
        {
            errors.Add($"'{Section}:CurrencyDecimals' must be 18, got '{options.CurrencyDecimals}'.");
        }

        if (options.RpcUrls.Count == 0)
        {
            errors.Add($"'{Section}:RpcUrls' must list at least one public HTTPS RPC URL.");
        }

        ValidateUrls(options.RpcUrls, "RpcUrls", errors);
        ValidateUrls(options.BlockExplorerUrls, "BlockExplorerUrls", errors);

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }

    /// <summary>
    /// URLs are shown to every browser, so they must be plain public HTTPS addresses. Credentials and query
    /// strings are rejected because that is where provider API keys usually live.
    /// </summary>
    private static void ValidateUrls(List<string> urls, string key, List<string> errors)
    {
        for (var index = 0; index < urls.Count; index++)
        {
            var valid = Uri.TryCreate(urls[index], UriKind.Absolute, out var uri)
                && uri.Scheme == Uri.UriSchemeHttps
                && uri.UserInfo.Length == 0
                && uri.Query.Length == 0
                && uri.Fragment.Length == 0;

            if (!valid)
            {
                errors.Add($"'{Section}:{key}:{index}' must be a public https:// URL without credentials or query string.");
            }
        }
    }
}
