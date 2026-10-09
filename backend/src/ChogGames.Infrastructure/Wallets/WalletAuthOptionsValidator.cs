using Microsoft.Extensions.Options;

namespace ChogGames.Infrastructure.Wallets;

public sealed class WalletAuthOptionsValidator : IValidateOptions<WalletAuthOptions>
{
    private const string Section = WalletAuthOptions.SectionName;

    public ValidateOptionsResult Validate(string? name, WalletAuthOptions options)
    {
        var errors = new List<string>();

        var validUri = System.Uri.TryCreate(options.Uri, UriKind.Absolute, out var uri)
            && (uri.Scheme == System.Uri.UriSchemeHttps || (uri.Scheme == System.Uri.UriSchemeHttp && uri.IsLoopback))
            && uri.UserInfo.Length == 0
            && uri.AbsolutePath == "/"
            && uri.Query.Length == 0
            && uri.Fragment.Length == 0;
        if (!validUri)
        {
            errors.Add($"'{Section}:Uri' must be the web app origin, e.g. 'https://app.example.com' (http only for localhost), got '{options.Uri}'.");
        }
        else if (!string.Equals(options.Domain, uri!.Authority, StringComparison.OrdinalIgnoreCase))
        {
            errors.Add($"'{Section}:Domain' must be the host[:port] of '{Section}:Uri' ('{uri.Authority}'), got '{options.Domain}'.");
        }

        if (options.ChallengeLifetime < TimeSpan.FromMinutes(1) || options.ChallengeLifetime > TimeSpan.FromMinutes(15))
        {
            errors.Add($"'{Section}:ChallengeLifetime' must be between 00:01:00 and 00:15:00, got '{options.ChallengeLifetime}'.");
        }

        if (options.RequestsPerMinutePerClient <= 0)
        {
            errors.Add($"'{Section}:RequestsPerMinutePerClient' must be positive.");
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
