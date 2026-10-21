using ChogGames.Core.Abilities;
using ChogGames.Core.Nfts;
using ChogGames.Core.Players;
using ChogGames.Core.Wallets;
using static ChogGames.Core.Tests.Abilities.FakeAbilityGame;

namespace ChogGames.Core.Tests.Abilities;

/// <summary>
/// STORY-090, DEC-077: everyone may choose a game's standard abilities and holders all of them, each with the values of
/// the character's Tier. Guests, standard Chogs, Common holders and Chogs without a readable Tier get the Base values; a
/// game without rules is offered whole, as defined.
/// </summary>
public class HolderAbilityStrategyTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

    private static readonly HolderAbilityRules Rules = new(
        "Tier",
        new Dictionary<string, HolderGameRules>
        {
            [GameType] = new(
                new HashSet<string> { "steal" },
                new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyDictionary<string, decimal>>>
                {
                    [HolderAbilityRules.BaseTier] = Tier(("boost", "percent", 30)),
                    ["rare"] = Tier(("boost", "percent", 40), ("steal", "uses", 2)),
                    ["legendary"] = Tier(("boost", "percent", 50), ("boost", "uses", 3)),
                }),
        });

    private readonly AbilityPool _pool = new(new AbilityEngine([Catalog()]), new HolderAbilityStrategy(Rules));

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, decimal>> Tier(params (string Ability, string Name, decimal Value)[] values) =>
        values.GroupBy(v => v.Ability).ToDictionary(
            g => g.Key,
            g => (IReadOnlyDictionary<string, decimal>)g.ToDictionary(v => v.Name, v => v.Value));

    private static CharacterSnapshot Guest() => CharacterSnapshot.Standard(DefaultAvatar.Classic, Now);

    private static CharacterSnapshot Holder(params NftTrait[] traits)
    {
        Assert.True(WalletAddress.TryParse("0x" + new string('d', 40), out var wallet));
        return CharacterSnapshot.Nft(wallet, new OwnedNft(NftTokenId.From(7), "Chog #7", null, traits), Now);
    }

    private static (string Id, decimal? Percent, int Uses)[] Offered(IEnumerable<AbilityDefinition> abilities) =>
        [.. abilities.Select(a => (a.Id, a.Parameters.TryGetValue("percent", out var p) ? p : (decimal?)null, a.MaxUses))];

    [Fact]
    public void A_guest_and_a_standard_chog_get_only_the_standard_abilities_with_base_values()
    {
        Assert.Equal([("steal", null, 1)], Offered(_pool.For(GameType, Guest())));
        Assert.False(_pool.Offers(GameType, Guest(), "boost"));
        Assert.Null(_pool.Lock(GameType, Guest(), "boost"));
    }

    [Theory]
    [InlineData("Common")]
    [InlineData("Mythic")]
    public void A_common_holder_or_an_unknown_tier_gets_every_ability_with_base_values(string tier)
    {
        Assert.Equal([("boost", 30m, 1), ("steal", null, 1)], Offered(_pool.For(GameType, Holder(new NftTrait("Tier", tier)))));
    }

    [Fact]
    public void A_holder_without_a_readable_tier_gets_base_values()
    {
        Assert.Equal([("boost", 30m, 1), ("steal", null, 1)], Offered(_pool.For(GameType, Holder())));
        Assert.Equal(
            [("boost", 30m, 1), ("steal", null, 1)],
            Offered(_pool.For(GameType, Holder(new NftTrait("Tier", "Rare"), new NftTrait("Tier", "Legendary")))));
        Assert.Equal([("boost", 30m, 1), ("steal", null, 1)], Offered(_pool.For(GameType, Holder(new NftTrait("Rarity", "Legendary")))));
    }

    [Fact]
    public void Rarer_tiers_get_their_values_and_uses_however_the_trait_is_spelt()
    {
        Assert.Equal([("boost", 40m, 1), ("steal", null, 2)], Offered(_pool.For(GameType, Holder(new NftTrait(" tier ", "RARE")))));
        Assert.Equal([("boost", 50m, 3), ("steal", null, 1)], Offered(_pool.For(GameType, Holder(new NftTrait("Tier", "Legendary")))));
    }

    [Fact]
    public void The_tier_values_are_locked_with_the_catalog_version()
    {
        var locked = _pool.Lock(GameType, Holder(new NftTrait("Tier", "Legendary")), "boost")!;

        Assert.Equal((1, 50m, 3), (locked.CatalogVersion, locked.Parameters["percent"], locked.MaxUses));
    }

    [Fact]
    public void A_game_without_rules_is_offered_whole_as_defined()
    {
        var pool = new AbilityPool(new AbilityEngine([Catalog()]), new HolderAbilityStrategy(new HolderAbilityRules("Tier", new Dictionary<string, HolderGameRules>())));

        Assert.Equal([("boost", 25m, 1), ("steal", null, 1)], Offered(pool.For(GameType, Guest())));
        Assert.Equal(Catalog().Definitions, pool.For(GameType, Holder(new NftTrait("Tier", "Legendary"))));
    }

    [Fact]
    public void Tier_values_outside_the_handlers_range_are_dropped_by_the_pool()
    {
        var pool = new AbilityPool(new AbilityEngine([Catalog(new BoostHandler(maxPercent: 45))]), new HolderAbilityStrategy(Rules));

        Assert.Equal(["steal"], pool.For(GameType, Holder(new NftTrait("Tier", "Legendary"))).Select(a => a.Id));
        Assert.Equal(["boost", "steal"], pool.For(GameType, Holder(new NftTrait("Tier", "Rare"))).Select(a => a.Id));
    }
}
