using ChogGames.Core.Abilities;
using ChogGames.Core.Nfts;
using ChogGames.Core.Players;
using ChogGames.Core.Wallets;
using static ChogGames.Core.Tests.Abilities.FakeAbilityGame;

namespace ChogGames.Core.Tests.Abilities;

/// <summary>
/// STORY-031: the shared ability pool. Guests and NFT holders, whatever their traits, see the same abilities of the
/// game; a game without a catalog offers none; and the pool never offers more than the game's catalog, whatever the
/// strategy returns.
/// </summary>
public class AbilityPoolTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

    private readonly AbilityPool _pool = new(new AbilityEngine([Catalog()]), new SharedAbilityPoolStrategy());

    private static CharacterSnapshot Guest() => CharacterSnapshot.Standard(DefaultAvatar.Classic, Now);

    private static CharacterSnapshot NftChog(ulong tokenId, params NftTrait[] traits)
    {
        Assert.True(WalletAddress.TryParse("0x" + new string('b', 40), out var wallet));
        return CharacterSnapshot.Nft(wallet, new OwnedNft(NftTokenId.From(tokenId), $"Chog #{tokenId}", null, traits), Now);
    }

    private static string[] Ids(IEnumerable<AbilityDefinition> abilities) => [.. abilities.Select(a => a.Id)];

    [Fact]
    public void Guests_and_nft_holders_see_the_same_pool_whatever_their_traits()
    {
        var guest = _pool.For(GameType, Guest());
        var plain = _pool.For(GameType, NftChog(7));
        var legendary = _pool.For(GameType, NftChog(8, new NftTrait("Rarity", "Legendary"), new NftTrait("Background", "Gold")));
        var common = _pool.For(GameType, NftChog(9, new NftTrait("Rarity", "Common")));

        Assert.Equal(["boost", "steal"], Ids(guest));
        Assert.Equal(guest, plain);
        Assert.Equal(guest, legendary);
        Assert.Equal(guest, common);
        Assert.Equal(Catalog().Definitions, guest);
    }

    [Fact]
    public void A_new_trait_does_not_change_the_pool()
    {
        var before = _pool.For(GameType, NftChog(7, new NftTrait("Rarity", "Rare")));
        var after = _pool.For(GameType, NftChog(7, new NftTrait("Rarity", "Rare"), new NftTrait("Aura", "Shimmering")));

        Assert.Equal(before, after);
    }

    [Fact]
    public void The_shared_strategy_returns_the_whole_catalog_and_never_reads_the_character()
    {
        var catalog = Catalog();
        var strategy = new SharedAbilityPoolStrategy();

        Assert.Same(catalog.Definitions, strategy.AvailableAbilities(catalog, Guest()));
        Assert.Same(catalog.Definitions, strategy.AvailableAbilities(catalog, NftChog(7, new NftTrait("Rarity", "Legendary"))));
        Assert.Throws<ArgumentNullException>(() => strategy.AvailableAbilities(null!, Guest()));
        Assert.Throws<ArgumentNullException>(() => strategy.AvailableAbilities(catalog, null!));
    }

    [Fact]
    public void Each_player_can_select_any_ability_of_the_pool()
    {
        foreach (var character in new[] { Guest(), NftChog(7, new NftTrait("Rarity", "Legendary")) })
        {
            Assert.True(_pool.Offers(GameType, character, "boost"));
            Assert.True(_pool.Offers(GameType, character, "steal"));
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("card_steal")]
    [InlineData("BOOST")]
    [InlineData(" boost")]
    [InlineData("boost\0")]
    public void Unknown_or_malformed_abilities_are_not_offered(string? abilityId)
    {
        Assert.False(_pool.Offers(GameType, Guest(), abilityId));
        Assert.False(_pool.Offers(GameType, NftChog(7), abilityId));
    }

    [Theory]
    [InlineData("chog-life")]
    [InlineData("unknown-game")]
    [InlineData("")]
    public void A_game_without_a_catalog_offers_nothing(string gameType)
    {
        Assert.Empty(_pool.For(gameType, Guest()));
        Assert.False(_pool.Offers(gameType, Guest(), "boost"));
    }

    [Fact]
    public void Another_games_abilities_are_not_offered()
    {
        var other = AbilityCatalog.Create(
            "chog-other", 1,
            [AbilityDefinition.Create("shield", "Shield", "Block one attack.", AbilityTargetKind.None, ["incoming_attack"])],
            [new NamedHandler("shield")], new StateWindow());
        var pool = new AbilityPool(new AbilityEngine([Catalog(), other]), new SharedAbilityPoolStrategy());

        Assert.Equal(["boost", "steal"], Ids(pool.For(GameType, Guest())));
        Assert.Equal(["shield"], Ids(pool.For("chog-other", Guest())));
        Assert.False(pool.Offers(GameType, Guest(), "shield"));
        Assert.False(pool.Offers("chog-other", Guest(), "boost"));
    }

    [Fact]
    public void The_pool_never_offers_more_than_the_catalog_whatever_the_strategy_returns()
    {
        var outOfRange = Boost(percent: 51);
        var retargeted = AbilityDefinition.Create(
            "steal", "Steal", "Take a random card from an opponent.", AbilityTargetKind.None, ["own_turn", "before_reward"]);
        var foreign = AbilityDefinition.Create("shield", "Shield", "Block one attack.", AbilityTargetKind.None, ["incoming_attack"]);
        var catalog = Catalog(new BoostHandler(maxPercent: 50));
        var pool = new AbilityPool(new AbilityEngine([catalog]), new FixedStrategy([foreign, outOfRange, retargeted, Steal(), Steal(maxUses: 3), null!]));

        var offered = pool.For(GameType, Guest());

        Assert.Equal(["steal"], Ids(offered));
        Assert.Equal(1, offered[0].MaxUses);
        Assert.Equal(AbilityTargetKind.Opponent, offered[0].Target);
        Assert.False(pool.Offers(GameType, Guest(), "shield"));
        Assert.False(pool.Offers(GameType, Guest(), "boost"));
    }

    [Fact]
    public void The_pool_keeps_a_strategys_values_within_the_handlers_ranges()
    {
        var stronger = Boost(percent: 40).WithValues(new Dictionary<string, decimal> { ["percent"] = 40 }, maxUses: 2);
        var pool = new AbilityPool(new AbilityEngine([Catalog()]), new FixedStrategy([stronger]));

        var offered = Assert.Single(pool.For(GameType, Guest()));
        var locked = pool.Lock(GameType, Guest(), "boost")!;

        Assert.Equal(40, offered.Parameters["percent"]);
        Assert.Equal(2, offered.MaxUses);
        Assert.Equal(40, locked.Parameters["percent"]);
        Assert.Equal(2, locked.MaxUses);
        Assert.Equal(1, locked.CatalogVersion);
    }

    [Fact]
    public void A_strategy_may_narrow_the_pool()
    {
        var pool = new AbilityPool(new AbilityEngine([Catalog()]), new FixedStrategy([Steal()]));

        Assert.Equal(["steal"], Ids(pool.For(GameType, Guest())));
        Assert.False(pool.Offers(GameType, Guest(), "boost"));
    }

    [Fact]
    public void Missing_arguments_are_refused()
    {
        Assert.Throws<ArgumentNullException>(() => _pool.For(null!, Guest()));
        Assert.Throws<ArgumentNullException>(() => _pool.For(GameType, null!));
    }

    /// <summary>A stand-in for a future strategy that returns whatever it was given.</summary>
    private sealed class FixedStrategy(IReadOnlyList<AbilityDefinition> abilities) : IAbilityAssignmentStrategy
    {
        public IReadOnlyList<AbilityDefinition> AvailableAbilities(AbilityCatalog catalog, CharacterSnapshot character) => abilities;
    }

    private sealed class NamedHandler(string abilityId) : IAbilityHandler
    {
        public string AbilityId => abilityId;

        public IReadOnlyList<AbilityParameter> Parameters { get; } = [];

        public string? CanExecute(AbilityContext context) => null;

        public System.Text.Json.JsonElement Execute(AbilityContext context) => context.State!.Value;
    }

    [Fact]
    public void Lock_snapshots_an_offered_ability_with_the_catalog_version()
    {
        var locked = _pool.Lock(GameType, NftChog(7, new NftTrait("Rarity", "Rare")), "steal");

        Assert.Equal(AbilitySnapshot.Lock(Catalog(), Steal()), locked);
        Assert.Equal(Catalog().Version, locked!.CatalogVersion);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("teleport")]
    [InlineData("Boost")]
    public void Lock_refuses_what_the_pool_does_not_offer(string? abilityId)
    {
        Assert.Null(_pool.Lock(GameType, Guest(), abilityId));
        Assert.Null(_pool.Lock("chog-race", Guest(), "boost"));
    }
}
