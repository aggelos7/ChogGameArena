using System.Text.Json;
using ChogGames.Core.Abilities;
using ChogGames.Core.Nfts;
using ChogGames.Core.Players;
using ChogGames.Core.Wallets;
using ChogGames.Infrastructure.Abilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace ChogGames.Api.Tests.Abilities;

/// <summary>
/// STORY-031, DEC-033: a game's abilities reach the API by registering the game's <see cref="AbilityCatalog"/>, with no
/// other change; STORY-090, DEC-077: the holder strategy is the registered one, and a game it has no rules for is
/// offered whole to everyone, as with the shared pool. STORY-032, DEC-034: a trait-based strategy
/// replaces that one registration, and nothing else. STORY-061, DEC-066: it does so only when
/// <c>Abilities:TraitBased:Enabled</c> is on, and the rules are checked against the catalogs at startup.
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
    public async Task The_holder_strategy_is_the_only_strategy()
    {
        await using var factory = new ApiFactory(ApiFactory.UnusedDatabase);

        Assert.IsType<HolderAbilityStrategy>(Assert.Single(factory.Services.GetServices<IAbilityAssignmentStrategy>()));
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
            s.AddSingleton<IAbilityAssignmentStrategy>(new TraitBasedAbilityStrategy(new LegendaryRerollResolver()));
        });
        var pool = factory.Services.GetRequiredService<AbilityPool>();

        Assert.Equal(["event_reroll"], pool.For(GameType, NftChog()).Select(a => a.Id));
        Assert.Equal(["reward_boost", "event_reroll"], pool.For(GameType, CharacterSnapshot.Standard(DefaultAvatar.Classic, Now)).Select(a => a.Id));
        Assert.Same(factory.Services.GetRequiredService<AbilityEngine>().CatalogFor(GameType), Assert.Single(factory.Services.GetServices<AbilityCatalog>()));
    }

    private static Dictionary<string, string?> LegendaryRerolls(bool? enabled) => new()
    {
        ["Abilities:TraitBased:Enabled"] = enabled?.ToString(),
        ["Abilities:TraitBased:Rules:0:Game"] = GameType,
        ["Abilities:TraitBased:Rules:0:Traits:0:Type"] = "Rarity",
        ["Abilities:TraitBased:Rules:0:Traits:0:Value"] = "Legendary",
        ["Abilities:TraitBased:Rules:0:Abilities:0"] = "event_reroll",
    };

    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    public async Task Trait_rules_change_nothing_while_the_flag_is_off(bool? enabled)
    {
        await using var factory = new ApiFactory(ApiFactory.UnusedDatabase, LegendaryRerolls(enabled), s => s.AddSingleton(Catalog()));
        var pool = factory.Services.GetRequiredService<AbilityPool>();

        Assert.IsType<HolderAbilityStrategy>(Assert.Single(factory.Services.GetServices<IAbilityAssignmentStrategy>()));
        Assert.Equal(["reward_boost", "event_reroll"], pool.For(GameType, NftChog()).Select(a => a.Id));
    }

    [Fact]
    public async Task The_flag_swaps_in_the_trait_based_strategy_and_guests_keep_the_whole_pool()
    {
        await using var factory = new ApiFactory(ApiFactory.UnusedDatabase, LegendaryRerolls(true), s => s.AddSingleton(Catalog()));
        var pool = factory.Services.GetRequiredService<AbilityPool>();

        Assert.IsType<TraitBasedAbilityStrategy>(Assert.Single(factory.Services.GetServices<IAbilityAssignmentStrategy>()));
        Assert.Equal(["event_reroll"], pool.For(GameType, NftChog()).Select(a => a.Id));
        Assert.Equal(["reward_boost", "event_reroll"], pool.For(GameType, CharacterSnapshot.Standard(DefaultAvatar.Classic, Now)).Select(a => a.Id));
    }

    [Fact]
    public async Task The_flag_on_without_rules_gives_everyone_the_whole_pool()
    {
        await using var factory = new ApiFactory(
            ApiFactory.UnusedDatabase, new Dictionary<string, string?> { ["Abilities:TraitBased:Enabled"] = "true" }, s => s.AddSingleton(Catalog()));
        var pool = factory.Services.GetRequiredService<AbilityPool>();

        Assert.Equal(["reward_boost", "event_reroll"], pool.For(GameType, NftChog()).Select(a => a.Id));
    }

    [Fact]
    public void Rules_are_checked_at_startup_even_while_the_flag_is_off()
    {
        // The startup check here registers no game catalogs, so no rule's game has abilities.
        var failure = StartupValidation.Failure(LegendaryRerolls(false));

        Assert.Contains("'Abilities:TraitBased:Rules:0:Game' 'chog-test' has no ability catalog.", failure.Failures);
    }

    [Fact]
    public void Valid_rules_pass()
    {
        Assert.True(Validate(LegendaryRerollsOptions()).Succeeded);
        Assert.True(Validate(new TraitAbilityOptions { Enabled = true }).Succeeded);
    }

    public static TheoryData<string, Action<TraitAbilityOptions>> BadRules => new()
    {
        { "'Abilities:TraitBased:Rules:0:Game' 'chog-life' has no ability catalog.", o => o.Rules[0].Game = "chog-life" },
        { "'Abilities:TraitBased:Rules:0:Game' '' has no ability catalog.", o => o.Rules[0].Game = "" },
        { "'Abilities:TraitBased:Rules:0:Traits' must have 1–10 traits.", o => o.Rules[0].Traits.Clear() },
        { "'Abilities:TraitBased:Rules:0:Traits' must have 1–10 traits.", o => o.Rules[0].Traits.AddRange(Enumerable.Range(0, 10).Select(i => new TraitOptions { Type = "T", Value = $"{i}" })) },
        { "'Abilities:TraitBased:Rules:0:Traits' must each have a Type and a Value of 1–100 printable characters.", o => o.Rules[0].Traits[0].Type = "  " },
        { "'Abilities:TraitBased:Rules:0:Traits' must each have a Type and a Value of 1–100 printable characters.", o => o.Rules[0].Traits[0].Value = new string('x', 101) },
        { "'Abilities:TraitBased:Rules:0:Traits' must each have a Type and a Value of 1–100 printable characters.", o => o.Rules[0].Traits[0].Value = "Go\u0007ld" },
        { "'Abilities:TraitBased:Rules:0:Abilities' must name at least one ability.", o => o.Rules[0].Abilities.Clear() },
        { "'Abilities:TraitBased:Rules:0:Abilities' 'fireball' is not an ability of chog-test.", o => o.Rules[0].Abilities.Add("fireball") },
        { "'Abilities:TraitBased:Rules:0:Abilities' 'Event_Reroll' is not an ability of chog-test.", o => o.Rules[0].Abilities[0] = "Event_Reroll" },
        { "'Abilities:TraitBased:Rules' must have at most 200 rules.", o => o.Rules.AddRange(Enumerable.Range(0, 200).Select(_ => LegendaryRerollsOptions().Rules[0])) },
    };

    [Theory]
    [MemberData(nameof(BadRules))]
    public void A_bad_rule_stops_startup(string error, Action<TraitAbilityOptions> spoil)
    {
        var options = LegendaryRerollsOptions();
        spoil(options);

        var result = Validate(options);

        Assert.True(result.Failed);
        Assert.Contains(error, result.Failures!);
    }

    private static TraitAbilityOptions LegendaryRerollsOptions() => new()
    {
        Rules =
        [
            new TraitAbilityRuleOptions
            {
                Game = GameType,
                Traits = [new TraitOptions { Type = "Rarity", Value = "Legendary" }],
                Abilities = ["event_reroll"],
            },
        ],
    };

    private static ValidateOptionsResult Validate(TraitAbilityOptions options) =>
        new TraitAbilityOptionsValidator(new AbilityEngine([Catalog()])).Validate(null, options);

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
