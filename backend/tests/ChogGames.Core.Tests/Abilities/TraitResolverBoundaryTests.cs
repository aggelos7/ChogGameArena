using System.Reflection;
using ChogGames.Core.Abilities;
using ChogGames.Core.Nfts;
using ChogGames.Core.Players;
using ChogGames.Core.Wallets;
using static ChogGames.Core.Tests.Abilities.FakeAbilityGame;

namespace ChogGames.Core.Tests.Abilities;

/// <summary>
/// STORY-032, DEC-034: the trait-ready boundary. A trait-based strategy built on <see cref="ITraitAbilityResolver"/> plugs
/// into the same <see cref="AbilityPool"/> and <see cref="AbilityEngine"/> as the shared pool, with no change to either.
/// It sees only normalized traits, falls back to the whole catalog, and can never offer more than the catalog. The
/// strategy is the one that ships behind the flag (STORY-061, DEC-066); the resolvers here exist only in the tests.
/// </summary>
public class TraitResolverBoundaryTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

    private static CharacterSnapshot Guest() => CharacterSnapshot.Standard(DefaultAvatar.Classic, Now);

    private static CharacterSnapshot NftChog(params NftTrait[] traits)
    {
        Assert.True(WalletAddress.TryParse("0x" + new string('d', 40), out var wallet));
        return CharacterSnapshot.Nft(wallet, new OwnedNft(NftTokenId.From(11), "Chog #11", null, traits), Now);
    }

    private static AbilityPool PoolWith(ITraitAbilityResolver resolver) =>
        new(new AbilityEngine([Catalog()]), new TraitBasedAbilityStrategy(resolver));

    private static string[] Ids(IEnumerable<AbilityDefinition> abilities) => [.. abilities.Select(a => a.Id)];

    [Fact]
    public void A_trait_based_strategy_plugs_into_the_unchanged_pool()
    {
        var pool = PoolWith(new LegendaryStealResolver());

        Assert.Equal(["steal"], Ids(pool.For(GameType, NftChog(new NftTrait("Rarity", "Legendary")))));
        Assert.True(pool.Offers(GameType, NftChog(new NftTrait("rarity", " LEGENDARY")), "steal"));
        Assert.False(pool.Offers(GameType, NftChog(new NftTrait("Rarity", "Legendary")), "boost"));
    }

    [Fact]
    public void The_resolver_sees_only_normalized_traits()
    {
        var resolver = new RecordingResolver();
        var pool = PoolWith(resolver);

        pool.For(GameType, NftChog(new NftTrait("Eye  Color", "BLUE"), new NftTrait("eye color", "blue"), new NftTrait("Hat", "Cap")));

        var seen = Assert.Single(resolver.Seen);
        Assert.Equal([new NormalizedTrait("eye color", "blue"), new NormalizedTrait("hat", "cap")], seen.Traits);
    }

    [Fact]
    public void Players_whose_traits_map_to_nothing_get_the_whole_catalog()
    {
        var pool = PoolWith(new LegendaryStealResolver());

        Assert.Equal(["boost", "steal"], Ids(pool.For(GameType, Guest())));
        Assert.Equal(["boost", "steal"], Ids(pool.For(GameType, NftChog())));
        Assert.Equal(["boost", "steal"], Ids(pool.For(GameType, NftChog(new NftTrait("Rarity", "Common")))));
    }

    [Fact]
    public void Ids_the_catalog_does_not_define_are_never_offered()
    {
        var pool = PoolWith(new FixedResolver(["shield", "STEAL", "steal", "steal", ""]));

        Assert.Equal(["steal"], Ids(pool.For(GameType, NftChog(new NftTrait("Rarity", "Legendary")))));
        Assert.False(pool.Offers(GameType, NftChog(), "shield"));
    }

    [Fact]
    public void A_resolver_returning_only_unknown_ids_falls_back_to_the_catalog()
    {
        var pool = PoolWith(new FixedResolver(["shield", "fireball"]));

        Assert.Equal(["boost", "steal"], Ids(pool.For(GameType, NftChog(new NftTrait("Rarity", "Legendary")))));
        Assert.False(pool.Offers(GameType, NftChog(), "shield"));
    }

    [Fact]
    public void A_game_without_a_catalog_still_offers_nothing_and_never_asks_the_resolver()
    {
        var resolver = new RecordingResolver();
        var pool = PoolWith(resolver);

        Assert.Empty(pool.For("chog-life", NftChog(new NftTrait("Rarity", "Legendary"))));
        Assert.Empty(resolver.Seen);
    }

    [Fact]
    public void The_engine_and_the_pool_depend_on_no_strategy_or_trait_type()
    {
        Type[] traitTypes = [typeof(ITraitAbilityResolver), typeof(TraitSet), typeof(NormalizedTrait), typeof(NftTrait), typeof(SharedAbilityPoolStrategy)];

        var engineDependencies = typeof(AbilityEngine).GetConstructors().SelectMany(c => c.GetParameters()).Select(p => p.ParameterType);
        var poolDependencies = typeof(AbilityPool).GetConstructors().SelectMany(c => c.GetParameters()).Select(p => p.ParameterType);

        Assert.DoesNotContain(engineDependencies, t => traitTypes.Contains(t) || t == typeof(IAbilityAssignmentStrategy));
        Assert.Equal([typeof(AbilityEngine), typeof(IAbilityAssignmentStrategy)], poolDependencies);
        Assert.All(
            typeof(AbilityEngine).GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly),
            m => Assert.DoesNotContain(MemberTypes(m), traitTypes.Contains));
    }

    [Fact]
    public void The_resolver_contract_sees_no_wallet_token_room_or_match()
    {
        var parameters = typeof(ITraitAbilityResolver).GetMethods().SelectMany(m => m.GetParameters()).Select(p => p.ParameterType);

        Assert.Equal([typeof(AbilityCatalog), typeof(TraitSet)], parameters);
        Assert.All(typeof(TraitSet).GetProperties(BindingFlags.Public | BindingFlags.Instance).Concat(typeof(NormalizedTrait).GetProperties()), p =>
            Assert.Contains(p.PropertyType, new[] { typeof(IReadOnlyList<NormalizedTrait>), typeof(string) }));
    }

    private static IEnumerable<Type> MemberTypes(MemberInfo member) => member switch
    {
        FieldInfo f => [f.FieldType],
        PropertyInfo p => [p.PropertyType],
        MethodBase m => m.GetParameters().Select(p => p.ParameterType).Concat(m is MethodInfo mi ? [mi.ReturnType] : []),
        _ => [],
    };

    /// <summary>Legendary Chogs may only steal; everyone else gets the fallback.</summary>
    private sealed class LegendaryStealResolver : ITraitAbilityResolver
    {
        public IReadOnlyList<string> EligibleAbilityIds(AbilityCatalog catalog, TraitSet traits) =>
            traits.Has("rarity", "legendary") ? ["steal"] : [];
    }

    private sealed class FixedResolver(IReadOnlyList<string> ids) : ITraitAbilityResolver
    {
        public IReadOnlyList<string> EligibleAbilityIds(AbilityCatalog catalog, TraitSet traits) => ids;
    }

    private sealed class RecordingResolver : ITraitAbilityResolver
    {
        public List<TraitSet> Seen { get; } = [];

        public IReadOnlyList<string> EligibleAbilityIds(AbilityCatalog catalog, TraitSet traits)
        {
            Seen.Add(traits);
            return [];
        }
    }
}
