using System.Text.Json;
using ChogGames.Core.Abilities;
using ChogGames.Core.Nfts;
using ChogGames.Core.Players;
using ChogGames.Core.Wallets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ChogGames.Api.Tests.Abilities;

/// <summary>
/// STORY-031, DEC-033: the API uses the shared ability pool for every player, and a game's abilities reach it by
/// registering the game's <see cref="AbilityCatalog"/>, with no other change. STORY-032, DEC-034: a trait-based strategy
/// would replace that one registration, and nothing else.
/// </summary>
public class AbilityPoolRegistrationTests
{
    private const string GameType = "chog-test";

    private static readonly DateTimeOffset Now = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

    private static AbilityCatalog Catalog() => AbilityCatalog.Create(
        GameType, 1,
        [
            AbilityDefinition.Create("reward_boost", "Reward Boost", "Raise one reward.", AbilityTargetKind.None, ["before_reward"]),
            AbilityDefinition.Create("event_reroll", "Lucky Reroll", "Reroll one event.", AbilityTargetKind.None, ["after_event"]),
        ],
        [new NoEffect("reward_boost"), new NoEffect("event_reroll")],
        new NoWindow());

    private static CharacterSnapshot NftChog()
    {
        Assert.True(WalletAddress.TryParse("0x" + new string('c', 40), out var wallet));
        return CharacterSnapshot.Nft(wallet, new OwnedNft(NftTokenId.From(42), "Chog #42", null, [new NftTrait("Rarity", "Legendary")]), Now);
    }

    [Fact]
    public async Task The_shared_pool_is_the_only_strategy()
    {
        await using var factory = new ApiFactory(ApiFactory.UnusedDatabase);

        Assert.IsType<SharedAbilityPoolStrategy>(Assert.Single(factory.Services.GetServices<IAbilityAssignmentStrategy>()));
    }

    [Fact]
    public async Task No_game_offers_abilities_until_it_registers_a_catalog()
    {
        await using var factory = new ApiFactory(ApiFactory.UnusedDatabase);
        var pool = factory.Services.GetRequiredService<AbilityPool>();

        Assert.Empty(pool.For("chog-life", CharacterSnapshot.Standard(DefaultAvatar.Classic, Now)));
        Assert.Empty(pool.For("chog-race", NftChog()));
    }

    [Fact]
    public async Task A_registered_catalog_is_offered_alike_to_guests_and_nft_holders()
    {
        await using var factory = new ApiFactory(ApiFactory.UnusedDatabase, configureServices: s => s.AddSingleton(Catalog()));
        var pool = factory.Services.GetRequiredService<AbilityPool>();

        var guest = pool.For(GameType, CharacterSnapshot.Standard(DefaultAvatar.Classic, Now));
        var holder = pool.For(GameType, NftChog());

        Assert.Equal(["reward_boost", "event_reroll"], guest.Select(a => a.Id));
        Assert.Equal(guest, holder);
    }

    [Fact]
    public async Task Swapping_the_strategy_registration_is_the_only_change_a_trait_resolver_needs()
    {
        await using var factory = new ApiFactory(ApiFactory.UnusedDatabase, configureServices: s =>
        {
            s.AddSingleton(Catalog());
            s.RemoveAll<IAbilityAssignmentStrategy>();
            s.AddSingleton<IAbilityAssignmentStrategy>(new TraitBasedTestStrategy(new LegendaryRerollResolver()));
        });
        var pool = factory.Services.GetRequiredService<AbilityPool>();

        Assert.Equal(["event_reroll"], pool.For(GameType, NftChog()).Select(a => a.Id));
        Assert.Equal(["reward_boost", "event_reroll"], pool.For(GameType, CharacterSnapshot.Standard(DefaultAvatar.Classic, Now)).Select(a => a.Id));
        Assert.Same(factory.Services.GetRequiredService<AbilityEngine>().CatalogFor(GameType), Assert.Single(factory.Services.GetServices<AbilityCatalog>()));
    }

    /// <summary>A stand-in for a future trait-based strategy (DEC-034): resolver ids, else the whole catalog.</summary>
    private sealed class TraitBasedTestStrategy(ITraitAbilityResolver resolver) : IAbilityAssignmentStrategy
    {
        public IReadOnlyList<AbilityDefinition> AvailableAbilities(AbilityCatalog catalog, CharacterSnapshot character)
        {
            var eligible = resolver.EligibleAbilityIds(catalog, TraitSet.From(character));
            var chosen = catalog.Definitions.Where(d => eligible.Contains(d.Id)).ToList();
            return chosen.Count == 0 ? catalog.Definitions : chosen;
        }
    }

    private sealed class LegendaryRerollResolver : ITraitAbilityResolver
    {
        public IReadOnlyList<string> EligibleAbilityIds(AbilityCatalog catalog, TraitSet traits) =>
            traits.Has("rarity", "legendary") ? ["event_reroll"] : [];
    }

    private sealed class NoEffect(string abilityId) : IAbilityHandler
    {
        public string AbilityId => abilityId;

        public IReadOnlyList<AbilityParameter> Parameters { get; } = [];

        public string? CanExecute(AbilityContext context) => null;

        public JsonElement Execute(AbilityContext context) => context.State!.Value;
    }

    private sealed class NoWindow : IAbilityTiming
    {
        public string? CurrentWindow(AbilityContext context) => null;
    }
}
