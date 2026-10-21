using ChogGames.Core.Abilities;
using ChogGames.Core.Games;
using ChogGames.Games.ChogLife;
using ChogGames.Games.ChogLife.Abilities;
using ChogGames.Games.ChogLife.Rules;
using ChogGames.Games.ChogRace;
using ChogGames.Games.ChogRace.Abilities;
using ChogGames.Games.ChogRace.Rules;
using ChogGames.Infrastructure.Abilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace ChogGames.Api.Tests.Abilities;

/// <summary>
/// STORY-090, DEC-077: <c>Abilities:Holder</c> as shipped holds DEC-077's split and Tier table and passes the startup
/// check against the real catalogs; any bad value stops the API.
/// </summary>
public class HolderAbilityOptionsTests
{
    private static readonly ChogLifeMatchRules Life = new([LifeRulesV1.Rules, LifeRulesV2.Rules]);

    private static readonly ChogRaceMatchRules Race = new([RaceRulesV1.Rules]);

    private static readonly AbilityEngine Engine = new([LifeAbilities.Catalog(Life), RaceAbilities.Catalog(Race)]);

    /// <summary>The API's own appsettings.json, copied next to the tests with the API's build output.</summary>
    private static HolderAbilityOptions Shipped()
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), optional: false)
            .Build();
        return configuration.GetSection(HolderAbilityOptions.SectionName).Get<HolderAbilityOptions>()!;
    }

    private static ValidateOptionsResult Validate(HolderAbilityOptions options) =>
        new HolderAbilityOptionsValidator(Engine, [Life, Race]).Validate(null, options);

    [Fact]
    public void The_shipped_table_passes_and_holds_dec_077_and_dec_078()
    {
        var shipped = Shipped();

        Assert.True(Validate(shipped).Succeeded);
        Assert.Equal(["reward_boost", "loss_protection"], shipped.Games[GameDefinition.ChogLife].Standard);
        Assert.Equal(["attack_block", "card_preview"], shipped.Games[GameDefinition.ChogRace].Standard);
        string Row(string game, string ability, string value) => string.Join(
            " ",
            new[] { "Base", "Uncommon", "Rare", "Epic", "Legendary" }.Select(tier => shipped.Games[game].Tiers[tier][ability][value]));
        Assert.Equal("25 30 35 40 50", Row(GameDefinition.ChogLife, "reward_boost", "percent"));
        Assert.Equal("50 45 40 35 25", Row(GameDefinition.ChogLife, "loss_protection", "percent"));
        Assert.Equal("1 1 1 2 2", Row(GameDefinition.ChogLife, "event_preview", "uses"));
        Assert.Equal("2 2 3 3 3", Row(GameDefinition.ChogLife, "event_reroll", "draws"));
        Assert.Equal("2 2 3 3 3", Row(GameDefinition.ChogLife, "double_dice", "dice"));
        Assert.Equal("10 12 15 18 20", Row(GameDefinition.ChogLife, "tax_the_rich", "percent"));
        Assert.Equal("50 50 50 50 50", Row(GameDefinition.ChogLife, "tax_the_rich", "cap"));
        Assert.Equal("1 1 2 2 2", Row(GameDefinition.ChogRace, "attack_block", "blocks"));
        Assert.Equal("1 1 2 2 3", Row(GameDefinition.ChogRace, "card_preview", "cards"));
        Assert.Equal("2 3 3 4 4", Row(GameDefinition.ChogRace, "movement_boost", "tiles"));
        Assert.Equal("1 1 1 2 2", Row(GameDefinition.ChogRace, "card_steal", "cards"));
        Assert.Equal("Tier", shipped.TraitType);
    }

    [Fact]
    public void The_base_tier_matches_the_catalogs_own_values()
    {
        var shipped = Shipped();
        foreach (var catalog in new[] { Engine.CatalogFor(GameDefinition.ChogLife)!, Engine.CatalogFor(GameDefinition.ChogRace)! })
        {
            foreach (var (ability, values) in shipped.Games[catalog.GameType].Tiers[HolderAbilityOptions.BaseTier])
            {
                Assert.True(catalog.TryFind(ability, out var defined, out _));
                foreach (var (name, value) in values)
                {
                    Assert.Equal(value, name == AbilityDefinition.Uses ? defined.MaxUses : defined.Parameters[name]);
                }
            }
        }
    }

    public static TheoryData<string, Action<HolderAbilityOptions>> BadOptions => new()
    {
        { "'Abilities:Holder:Games:chog-royal' 'chog-royal' is not a game.", o => o.Games["chog-royal"] = new HolderGameOptions { Standard = ["x"] } },
        { "'Abilities:Holder:Games:chog-life:Standard' must name at least one ability, so everyone can ready.", o => o.Games["chog-life"].Standard.Clear() },
        { "'Abilities:Holder:Games:chog-life:Standard' 'fireball' is not an ability of chog-life.", o => o.Games["chog-life"].Standard.Add("fireball") },
        { "'Abilities:Holder:Games:chog-life:Standard' names an ability twice.", o => o.Games["chog-life"].Standard.Add("reward_boost") },
        { "'Abilities:Holder:Games:chog-race:Tiers:Rare:movement_boost:tiles' must be 1 to 6.", o => o.Games["chog-race"].Tiers["Rare"]["movement_boost"]["tiles"] = 7 },
        { "'Abilities:Holder:Games:chog-race:Tiers:Rare:card_steal:cards' must be 1 to 5.", o => o.Games["chog-race"].Tiers["Rare"]["card_steal"]["cards"] = 0 },
        { "'Abilities:Holder:Games:chog-life:Tiers:Epic:reward_boost:percent' must be a whole number.", o => o.Games["chog-life"].Tiers["Epic"]["reward_boost"]["percent"] = 40.5m },
        { "'Abilities:Holder:Games:chog-life:Tiers:Epic:event_preview:uses' must be 1 to 10.", o => o.Games["chog-life"].Tiers["Epic"]["event_preview"]["uses"] = 11 },
        { "'Abilities:Holder:Games:chog-life:Tiers:Epic:event_preview:range' is not a value of event_preview.", o => o.Games["chog-life"].Tiers["Epic"]["event_preview"]["range"] = 2 },
        { "'Abilities:Holder:Games:chog-life:Tiers:Epic:fireball' 'fireball' is not an ability of chog-life.", o => o.Games["chog-life"].Tiers["Epic"]["fireball"] = new() { ["percent"] = 1 } },
        { "'Abilities:Holder:Games:chog-life:Tiers:EPIC' must be a distinct Tier name of 1–100 printable characters.", o => o.Games["chog-life"].Tiers["EPIC"] = [] },
        { "'Abilities:Holder:TraitType' must have 1–100 printable characters.", o => o.TraitType = " " },
    };

    [Theory]
    [MemberData(nameof(BadOptions))]
    public void A_bad_value_stops_startup(string error, Action<HolderAbilityOptions> spoil)
    {
        var options = Shipped();
        spoil(options);

        var result = Validate(options);

        Assert.True(result.Failed);
        Assert.Contains(error, result.Failures!);
    }

    [Fact]
    public void A_bad_value_in_configuration_stops_the_api()
    {
        var failure = StartupValidation.Failure(new Dictionary<string, string?>
        {
            ["Abilities:Holder:Games:chog-race:Tiers:Epic:movement_boost:tiles"] = "9",
        });

        Assert.Contains("'Abilities:Holder:Games:chog-race:Tiers:Epic:movement_boost:tiles' must be 1 to 6.", failure.Failures);
    }

    [Fact]
    public void A_game_this_host_runs_without_abilities_is_skipped()
    {
        Assert.True(new HolderAbilityOptionsValidator(new AbilityEngine([]), [Life, Race]).Validate(null, Shipped()).Succeeded);
    }
}
