using ChogGames.Api.Wallet;

namespace ChogGames.Api.Tests.Wallet;

public class WalletChainOptionsValidatorTests
{
    private static WalletChainOptions Valid() => new()
    {
        ChainId = 10143,
        Name = "Monad Testnet",
        CurrencyName = "Monad",
        CurrencySymbol = "MON",
        CurrencyDecimals = 18,
        RpcUrls = ["https://testnet-rpc.monad.xyz"],
        BlockExplorerUrls = ["https://testnet.monadvision.com"],
    };

    private static IEnumerable<string> Errors(WalletChainOptions options) =>
        new WalletChainOptionsValidator().Validate(null, options).Failures ?? [];

    [Fact]
    public void Valid_chain_passes()
    {
        Assert.Empty(Errors(Valid()));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(9_007_199_254_740_992)]
    public void Chain_id_must_be_positive_and_browser_safe(long chainId)
    {
        var options = Valid();
        options.ChainId = chainId;

        Assert.Contains("'Wallet:Chain:ChainId' must be a positive EIP-155 chain id", Assert.Single(Errors(options)));
    }

    [Theory]
    [InlineData("M")]
    [InlineData("TOOLONG")]
    [InlineData("M N")]
    public void Currency_symbol_follows_eip_3085(string symbol)
    {
        var options = Valid();
        options.CurrencySymbol = symbol;

        Assert.Contains("'Wallet:Chain:CurrencySymbol' must be 2-6 characters", Assert.Single(Errors(options)));
    }

    [Fact]
    public void Currency_decimals_must_be_18()
    {
        var options = Valid();
        options.CurrencyDecimals = 6;

        Assert.Contains("'Wallet:Chain:CurrencyDecimals' must be 18", Assert.Single(Errors(options)));
    }

    [Fact]
    public void At_least_one_rpc_url_is_required()
    {
        var options = Valid();
        options.RpcUrls = [];

        Assert.Contains("must list at least one public HTTPS RPC URL", Assert.Single(Errors(options)));
    }

    [Theory]
    [InlineData("http://rpc.example.com")]
    [InlineData("rpc.example.com")]
    [InlineData("https://user:secret@rpc.example.com")]
    [InlineData("https://rpc.example.com/?apikey=secret")]
    [InlineData("javascript:alert(1)")]
    public void Rpc_urls_must_be_public_https_without_credentials_or_keys(string url)
    {
        var options = Valid();
        options.RpcUrls = ["https://ok.example.com", url];

        Assert.Contains("'Wallet:Chain:RpcUrls:1' must be a public https:// URL", Assert.Single(Errors(options)));
    }

    [Fact]
    public void Block_explorer_urls_are_validated_too()
    {
        var options = Valid();
        options.BlockExplorerUrls = ["ftp://explorer.example.com"];

        Assert.Contains("'Wallet:Chain:BlockExplorerUrls:0' must be a public https:// URL", Assert.Single(Errors(options)));
    }

    [Fact]
    public void Missing_section_reports_every_required_setting()
    {
        var errors = Errors(new WalletChainOptions()).ToArray();

        Assert.Contains(errors, e => e.Contains("'Wallet:Chain:ChainId'"));
        Assert.Contains(errors, e => e.Contains("'Wallet:Chain:Name' is required"));
        Assert.Contains(errors, e => e.Contains("'Wallet:Chain:CurrencyName' is required"));
        Assert.Contains(errors, e => e.Contains("'Wallet:Chain:CurrencySymbol'"));
        Assert.Contains(errors, e => e.Contains("'Wallet:Chain:RpcUrls'"));
    }
}
