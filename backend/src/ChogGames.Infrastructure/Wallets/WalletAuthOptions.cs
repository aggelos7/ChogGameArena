namespace ChogGames.Infrastructure.Wallets;

/// <summary>
/// Wallet sign-in settings (<c>WalletAuth</c>). <see cref="Domain"/> and <see cref="Uri"/> must be the address of
/// the web app users actually see, because wallets show it and warn when it differs from the page.
/// They are configuration, never taken from the request.
/// </summary>
public sealed class WalletAuthOptions
{
    public const string SectionName = "WalletAuth";

    /// <summary>EIP-4361 domain: host[:port] of the web app, e.g. <c>localhost:4201</c> or <c>app.example.com</c>.</summary>
    public string Domain { get; set; } = string.Empty;

    /// <summary>EIP-4361 URI: origin of the web app, e.g. <c>https://app.example.com</c>.</summary>
    public string Uri { get; set; } = string.Empty;

    public TimeSpan ChallengeLifetime { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Challenge and verify requests per client IP per minute (shared budget).</summary>
    public int RequestsPerMinutePerClient { get; set; } = 20;
}
