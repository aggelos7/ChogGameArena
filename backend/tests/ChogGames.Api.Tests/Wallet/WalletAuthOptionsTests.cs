using ChogGames.Infrastructure.Wallets;
using Microsoft.Extensions.Options;

namespace ChogGames.Api.Tests.Wallet;

public class WalletAuthOptionsTests
{
    private static IEnumerable<string> Errors(Action<WalletAuthOptions>? change = null)
    {
        var options = new WalletAuthOptions { Domain = "app.example.com", Uri = "https://app.example.com" };
        change?.Invoke(options);
        return new WalletAuthOptionsValidator().Validate(null, options).Failures ?? [];
    }

    [Fact]
    public void Valid_settings_pass_including_plain_http_on_localhost()
    {
        Assert.Empty(Errors());
        Assert.Empty(Errors(o => (o.Domain, o.Uri) = ("localhost:4200", "http://localhost:4200")));
        Assert.Empty(Errors(o => (o.Domain, o.Uri) = ("127.0.0.1:4200", "http://127.0.0.1:4200/")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("app.example.com")]
    [InlineData("http://app.example.com")]
    [InlineData("https://user:pw@app.example.com")]
    [InlineData("https://app.example.com/login")]
    [InlineData("https://app.example.com/?x=1")]
    public void Uri_must_be_an_https_origin(string uri)
    {
        Assert.Contains(Errors(o => o.Uri = uri), e => e.Contains("'WalletAuth:Uri' must be the web app origin"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("evil.example")]
    [InlineData("app.example.com:8443")]
    public void Domain_must_match_the_uri(string domain)
    {
        Assert.Contains("'WalletAuth:Domain' must be the host[:port] of 'WalletAuth:Uri'", Assert.Single(Errors(o => o.Domain = domain)));
    }

    [Theory]
    [InlineData("00:00:30")]
    [InlineData("00:16:00")]
    public void Challenge_lifetime_is_bounded(string lifetime)
    {
        Assert.Contains("'WalletAuth:ChallengeLifetime'", Assert.Single(Errors(o => o.ChallengeLifetime = TimeSpan.Parse(lifetime))));
    }

    [Fact]
    public void Rate_limit_must_be_positive()
    {
        Assert.Contains("'WalletAuth:RequestsPerMinutePerClient'", Assert.Single(Errors(o => o.RequestsPerMinutePerClient = 0)));
    }

    [Fact]
    public void Missing_wallet_auth_settings_stop_startup()
    {
        var error = StartupValidation.Failure(new Dictionary<string, string?>
        {
            ["WalletAuth:Domain"] = "",
            ["WalletAuth:Uri"] = "",
        });

        Assert.Contains("'WalletAuth:Uri' must be the web app origin", error.Message);
    }
}
