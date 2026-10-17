using ChogGames.Core.Abilities;
using ChogGames.Core.Nfts;
using ChogGames.Core.Players;
using ChogGames.Core.Wallets;
using static ChogGames.Core.Tests.Abilities.FakeAbilityGame;

namespace ChogGames.Core.Tests.Abilities;

/// <summary>
/// STORY-061, DEC-066: the configured trait rules. A rule needs every one of its traits; a Chog matching several rules
/// gets the union of their abilities; a Chog matching none, a standard Chog and a guest get the whole catalog.
/// </summary>
public class ConfiguredTraitAbilityResolverTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);

    private static TraitAbilityRule Rule(string[] abilities, params (string Type, string Value)[] traits) =>
        new(GameType, TraitSet.From(traits.Select(t => new NftTrait(t.Type, t.Value))), abilities);

    private static CharacterSnapshot NftChog(params (string Type, string Value)[] traits)
    {
        Assert.True(WalletAddress.TryParse("0x" + new string('e', 40), out var wallet));
        return CharacterSnapshot.Nft(
            wallet, new OwnedNft(NftTokenId.From(7), "Chog #7", null, [.. traits.Select(t => new NftTrait(t.Type, t.Value))]), Now);
    }

    private static string[] Offered(CharacterSnapshot character, params TraitAbilityRule[] rules)
    {
        var pool = new AbilityPool(new AbilityEngine([Catalog()]), new TraitBasedAbilityStrategy(new ConfiguredTraitAbilityResolver(rules)));
        return [.. pool.For(GameType, character).Select(a => a.Id)];
    }

    [Fact]
    public void A_rule_needs_every_one_of_its_traits()
    {
        var goldenKing = Rule(["steal"], ("Body", "Golden"), ("Head", "Crown"));

        Assert.Equal(["steal"], Offered(NftChog(("Body", "Golden"), ("Head", "Crown"), ("Eyes", "Laser")), goldenKing));
        Assert.Equal(["boost", "steal"], Offered(NftChog(("Body", "Golden")), goldenKing));
        Assert.Equal(["boost", "steal"], Offered(NftChog(("Body", "Golden"), ("Head", "Beanie")), goldenKing));
    }

    [Fact]
    public void Several_matching_rules_give_the_union_in_catalog_order()
    {
        TraitAbilityRule[] rules = [Rule(["steal"], ("Head", "Crown")), Rule(["boost"], ("Body", "Golden")), Rule(["steal"], ("Eyes", "Laser"))];

        Assert.Equal(["boost", "steal"], Offered(NftChog(("Body", "Golden"), ("Head", "Crown"), ("Eyes", "Laser")), rules));
        Assert.Equal(["steal"], Offered(NftChog(("Head", "Crown"), ("Eyes", "Laser")), rules));
        Assert.Equal(["boost"], Offered(NftChog(("Body", "Golden")), rules));
    }

    [Fact]
    public void Rule_and_metadata_traits_match_however_they_are_spelled()
    {
        var rule = new TraitAbilityRule(GameType, TraitSet.From([new NftTrait("  Eye  Color ", "LASER  Red")]), ["steal"]);

        Assert.Equal(["steal"], Offered(NftChog(("eye color", "laser red")), rule));
    }

    [Fact]
    public void Guests_standard_chogs_and_unmatched_holders_keep_the_whole_catalog()
    {
        var rule = Rule(["steal"], ("Rarity", "Legendary"));

        Assert.Equal(["boost", "steal"], Offered(CharacterSnapshot.Standard(DefaultAvatar.Classic, Now), rule));
        Assert.Equal(["boost", "steal"], Offered(NftChog(), rule));
        Assert.Equal(["boost", "steal"], Offered(NftChog(("Rarity", "Common")), rule));
        Assert.Equal(["boost", "steal"], Offered(NftChog(("Rarity", "Legendary"))));
    }

    [Fact]
    public void Only_the_games_own_rules_count()
    {
        var otherGame = new TraitAbilityRule("chog-other", TraitSet.From([new NftTrait("Rarity", "Legendary")]), ["steal"]);

        Assert.Equal(["boost", "steal"], Offered(NftChog(("Rarity", "Legendary")), otherGame));
    }

    [Fact]
    public void A_rule_without_traits_matches_nobody()
    {
        var empty = new TraitAbilityRule(GameType, TraitSet.Empty, ["steal"]);

        Assert.Equal(["boost", "steal"], Offered(NftChog(("Rarity", "Legendary")), empty));
        Assert.Equal(["boost", "steal"], Offered(CharacterSnapshot.Standard(DefaultAvatar.Classic, Now), empty));
    }

    [Fact]
    public void The_answer_is_deterministic()
    {
        var resolver = new ConfiguredTraitAbilityResolver([Rule(["steal", "boost", "steal"], ("Head", "Crown")), Rule(["boost"], ("Body", "Golden"))]);
        var traits = TraitSet.From([new NftTrait("Body", "Golden"), new NftTrait("Head", "Crown")]);

        var first = resolver.EligibleAbilityIds(Catalog(), traits);

        Assert.Equal(["steal", "boost"], first);
        Assert.All(Enumerable.Range(0, 20), _ => Assert.Equal(first, resolver.EligibleAbilityIds(Catalog(), traits)));
    }
}
