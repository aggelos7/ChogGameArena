using ChogGames.Core.Wallets;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace ChogGames.Infrastructure.Nfts;

public sealed class NftOptionsValidator(IHostEnvironment environment) : IValidateOptions<NftOptions>
{
    private const string Section = NftOptions.SectionName;

    /// <summary>Keeps a full scan to a bounded number of calls.</summary>
    public const long MaxScannedTokens = 20_000;

    public ValidateOptionsResult Validate(string? name, NftOptions options)
    {
        var errors = new List<string>();

        if (!Enum.IsDefined(options.Provider))
        {
            errors.Add($"'{Section}:Provider' must be one of: {string.Join(", ", Enum.GetNames<NftProviderKind>())}.");
        }
        else if (options.Provider == NftProviderKind.Mock && environment.IsProduction())
        {
            errors.Add($"'{Section}:Provider' 'Mock' reports fake ownership and is not allowed in Production.");
        }

        if (!Enum.IsDefined(options.Mock.OtherWallets))
        {
            errors.Add($"'{Section}:Mock:OtherWallets' must be one of: {string.Join(", ", Enum.GetNames<MockNftHolding>())}.");
        }

        if (options.Provider == NftProviderKind.Monad)
        {
            ValidateMonad(options.Monad, errors);
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }

    private static void ValidateMonad(MonadNftOptions monad, List<string> errors)
    {
        const string section = $"{Section}:Monad";

        // The URL is not echoed back: it may contain a provider key.
        var validUrl = Uri.TryCreate(monad.RpcUrl, UriKind.Absolute, out var url)
            && (url.Scheme == Uri.UriSchemeHttps || (url.Scheme == Uri.UriSchemeHttp && url.IsLoopback))
            && url.UserInfo.Length == 0;
        if (!validUrl)
        {
            errors.Add($"'{section}:RpcUrl' must be an https:// URL (http only for localhost) without user info.");
        }

        if (monad.ChainId <= 0)
        {
            errors.Add($"'{section}:ChainId' must be a positive EIP-155 chain id.");
        }

        if (!WalletAddress.TryParse(monad.ContractAddress, out _))
        {
            errors.Add($"'{section}:ContractAddress' must be a 0x-prefixed 20-byte address.");
        }

        if (!WalletAddress.TryParse(monad.Multicall3Address, out _))
        {
            errors.Add($"'{section}:Multicall3Address' must be a 0x-prefixed 20-byte address.");
        }

        if (monad.FirstTokenId < 0 || monad.LastTokenId < monad.FirstTokenId
            || monad.LastTokenId - monad.FirstTokenId >= MaxScannedTokens)
        {
            errors.Add($"'{section}:FirstTokenId'..'LastTokenId' must be a non-negative range of at most {MaxScannedTokens} tokens.");
        }

        if (monad.BatchSize is < 1 or > 1000)
        {
            errors.Add($"'{section}:BatchSize' must be between 1 and 1000.");
        }

        if (monad.OwnershipCacheDuration < TimeSpan.Zero || monad.OwnershipCacheDuration > TimeSpan.FromMinutes(10))
        {
            errors.Add($"'{section}:OwnershipCacheDuration' must be between 00:00:00 and 00:10:00.");
        }

        if (monad.RequestTimeout < TimeSpan.FromSeconds(1) || monad.RequestTimeout > TimeSpan.FromMinutes(1))
        {
            errors.Add($"'{section}:RequestTimeout' must be between 00:00:01 and 00:01:00.");
        }
    }
}
