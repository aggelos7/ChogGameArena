using System.Text;
using ChogGames.Core.Nfts;
using ChogGames.Core.Wallets;
using ChogGames.Infrastructure.Nfts;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Options;

namespace ChogGames.Infrastructure.Tests.Nfts;

public class MockNftProviderTests
{
    private static readonly WalletAddress Stranger = Address("0x5aAeb6053F3E94C9b9A09f33669435E7Ef1BeAed");

    private static WalletAddress Address(string value)
    {
        Assert.True(WalletAddress.TryParse(value, out var address));
        return address;
    }

    private static MockNftProvider Provider(MockNftHolding otherWallets = MockNftHolding.Many) =>
        new(Options.Create(new NftOptions { Provider = NftProviderKind.Mock, Mock = { OtherWallets = otherWallets } }));

    private static async Task<string[]> TokenIds(INftProvider provider, WalletAddress owner) =>
        [.. (await provider.GetOwnedAsync(owner, CancellationToken.None)).Select(nft => nft.TokenId.ToString())];

    [Fact]
    public async Task Fixture_wallets_own_zero_one_and_many_nfts()
    {
        var provider = Provider();

        Assert.Empty(await TokenIds(provider, MockNftFixtures.NoneHolder));
        Assert.Equal(["1"], await TokenIds(provider, MockNftFixtures.OneHolder));
        Assert.Equal(["7", "42", "108", "1969"], await TokenIds(provider, MockNftFixtures.ManyHolder));
    }

    [Fact]
    public async Task Many_fixture_covers_missing_image_unknown_traits_and_no_traits()
    {
        var nfts = await Provider().GetOwnedAsync(MockNftFixtures.ManyHolder, CancellationToken.None);

        Assert.Contains(nfts, nft => nft.ImageUrl is null);
        Assert.Contains(nfts, nft => nft.Traits.Count == 0);
        Assert.Contains(nfts, nft => nft.Traits.Any(trait => trait.Type == "Mystery Aura" && trait.Value == "Shimmering ✨"));
        Assert.Contains(nfts, nft => nft.Traits.Any(trait => trait.Value.Length == NftTrait.MaxLength));
        Assert.All(nfts, nft => Assert.False(string.IsNullOrWhiteSpace(nft.Name)));
        Assert.Equal(nfts.Select(nft => nft.TokenId).Order(), nfts.Select(nft => nft.TokenId));
        Assert.Equal(nfts.Count, nfts.Select(nft => nft.TokenId).Distinct().Count());
    }

    [Fact]
    public async Task Images_are_inline_svg_so_no_network_is_needed()
    {
        var images = (await Provider().GetOwnedAsync(MockNftFixtures.ManyHolder, CancellationToken.None))
            .Concat(await Provider().GetOwnedAsync(MockNftFixtures.OneHolder, CancellationToken.None))
            .Select(nft => nft.ImageUrl)
            .OfType<Uri>()
            .ToList();

        Assert.NotEmpty(images);
        Assert.All(images, image =>
        {
            Assert.Equal("data", image.Scheme);
            const string prefix = "data:image/svg+xml;base64,";
            Assert.StartsWith(prefix, image.OriginalString);
            Assert.StartsWith("<svg ", Encoding.UTF8.GetString(Convert.FromBase64String(image.OriginalString[prefix.Length..])));
        });
    }

    [Fact]
    public async Task Results_are_deterministic()
    {
        var first = await Provider().GetOwnedAsync(MockNftFixtures.ManyHolder, CancellationToken.None);
        var second = await Provider().GetOwnedAsync(MockNftFixtures.ManyHolder, CancellationToken.None);

        Assert.Equal(
            first.Select(nft => (nft.TokenId, nft.Name, nft.ImageUrl, string.Join('|', nft.Traits))),
            second.Select(nft => (nft.TokenId, nft.Name, nft.ImageUrl, string.Join('|', nft.Traits))));
    }

    [Theory]
    [InlineData(MockNftHolding.None, new string[0])]
    [InlineData(MockNftHolding.One, new[] { "1" })]
    [InlineData(MockNftHolding.Many, new[] { "7", "42", "108", "1969" })]
    public async Task Other_wallets_get_the_configured_holding(MockNftHolding holding, string[] expected)
    {
        Assert.Equal(expected, await TokenIds(Provider(holding), Stranger));
    }

    [Fact]
    public async Task Fixture_wallets_ignore_the_other_wallets_setting()
    {
        Assert.Empty(await TokenIds(Provider(MockNftHolding.Many), MockNftFixtures.NoneHolder));
        Assert.Equal(["7", "42", "108", "1969"], await TokenIds(Provider(MockNftHolding.None), MockNftFixtures.ManyHolder));
    }

    [Fact]
    public async Task Ownership_check_returns_only_tokens_the_wallet_owns()
    {
        var provider = Provider(MockNftHolding.None);

        var owned = await provider.FindOwnedAsync(MockNftFixtures.ManyHolder, NftTokenId.From(42), CancellationToken.None);
        Assert.Equal(MockNftFixtures.MissingImage, owned);

        // Exists in the fixtures but belongs to another wallet; does not exist; wallet owns nothing.
        Assert.Null(await provider.FindOwnedAsync(MockNftFixtures.ManyHolder, NftTokenId.From(1), CancellationToken.None));
        Assert.Null(await provider.FindOwnedAsync(MockNftFixtures.ManyHolder, NftTokenId.From(2), CancellationToken.None));
        Assert.Null(await provider.FindOwnedAsync(MockNftFixtures.NoneHolder, NftTokenId.From(7), CancellationToken.None));
        Assert.Null(await provider.FindOwnedAsync(Stranger, NftTokenId.From(7), CancellationToken.None));
    }

    [Fact]
    public async Task Simulated_outage_fails_with_a_controlled_error()
    {
        var provider = Provider();

        await Assert.ThrowsAsync<NftProviderUnavailableException>(() =>
            provider.GetOwnedAsync(MockNftFixtures.UnavailableHolder, CancellationToken.None));
        await Assert.ThrowsAsync<NftProviderUnavailableException>(() =>
            provider.FindOwnedAsync(MockNftFixtures.UnavailableHolder, NftTokenId.From(7), CancellationToken.None));
    }

    [Fact]
    public async Task Unverified_default_address_is_rejected()
    {
        var provider = Provider();

        await Assert.ThrowsAsync<ArgumentException>(() => provider.GetOwnedAsync(default, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => provider.FindOwnedAsync(default, NftTokenId.From(7), CancellationToken.None));
    }

    [Fact]
    public async Task Cancellation_is_honoured()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Provider().GetOwnedAsync(MockNftFixtures.ManyHolder, cancelled.Token));
    }

    [Fact]
    public async Task Unconfigured_provider_never_reports_ownership()
    {
        var provider = new UnconfiguredNftProvider();

        await Assert.ThrowsAsync<NftProviderUnavailableException>(() => provider.GetOwnedAsync(MockNftFixtures.ManyHolder, CancellationToken.None));
        await Assert.ThrowsAsync<NftProviderUnavailableException>(() =>
            provider.FindOwnedAsync(MockNftFixtures.ManyHolder, NftTokenId.From(7), CancellationToken.None));
    }

    [Theory]
    [InlineData("Development", NftProviderKind.Mock, true)]
    [InlineData("Testing", NftProviderKind.Mock, true)]
    [InlineData("Staging", NftProviderKind.Mock, true)]
    [InlineData("Production", NftProviderKind.Mock, false)]
    [InlineData("Production", NftProviderKind.None, true)]
    [InlineData("Development", (NftProviderKind)7, false)]
    public void Mock_is_refused_in_production(string environment, NftProviderKind kind, bool valid)
    {
        var validator = new NftOptionsValidator(new HostingEnvironment { EnvironmentName = environment });

        Assert.Equal(valid, validator.Validate(null, new NftOptions { Provider = kind }).Succeeded);
    }

    [Fact]
    public void Undefined_other_wallets_holding_is_refused()
    {
        var validator = new NftOptionsValidator(new HostingEnvironment { EnvironmentName = Environments.Development });

        var result = validator.Validate(null, new NftOptions { Provider = NftProviderKind.Mock, Mock = { OtherWallets = (MockNftHolding)9 } });

        Assert.True(result.Failed);
        Assert.Contains("'Nfts:Mock:OtherWallets'", result.FailureMessage);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(1000, true)]
    [InlineData(1001, false)]
    public void Per_player_request_limit_is_bounded(int limit, bool valid)
    {
        var validator = new NftOptionsValidator(new HostingEnvironment { EnvironmentName = Environments.Development });

        var result = validator.Validate(null, new NftOptions { Provider = NftProviderKind.Mock, RequestsPerMinutePerPlayer = limit });

        Assert.Equal(valid, result.Succeeded);
        if (!valid)
        {
            Assert.Contains("'Nfts:RequestsPerMinutePerPlayer'", result.FailureMessage);
        }
    }
}
