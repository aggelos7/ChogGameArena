using ChogGames.Api.ChogLife;
using ChogGames.Core.Abilities;
using ChogGames.Core.Matches;
using ChogGames.Games.ChogLife;
using ChogGames.Games.ChogLife.Abilities;
using ChogGames.Games.ChogLife.Rules;
using Microsoft.Extensions.DependencyInjection;

namespace ChogGames.Api.Tests.ChogLife;

/// <summary>
/// STORY-035, DEC-037: the API serves the current, checked Chog Life rules. STORY-042, DEC-044: it offers Chog Life's
/// abilities, acting on the same rules the match engine plays.
/// </summary>
public class ChogLifeRegistrationTests
{
    [Fact]
    public async Task The_api_registers_the_current_rules()
    {
        await using var factory = new ApiFactory(ApiFactory.UnusedDatabase);

        var rules = factory.Services.GetRequiredService<LifeRules>();

        Assert.Same(LifeRulesV1.Rules, rules);
        Assert.Equal(LifeRulesV1.Version, rules.Version);
    }

    [Fact]
    public void Chog_life_registers_its_ability_catalog_over_the_rules_it_plays()
    {
        using var services = new ServiceCollection().AddChogLife().AddSingleton<AbilityEngine>().BuildServiceProvider();

        var catalog = Assert.Single(services.GetServices<AbilityCatalog>());

        Assert.Equal((ChogGames.Core.Games.GameDefinition.ChogLife, LifeAbilities.Version), (catalog.GameType, catalog.Version));
        Assert.Same(catalog, services.GetRequiredService<AbilityEngine>().CatalogFor(ChogGames.Core.Games.GameDefinition.ChogLife));
        Assert.Same(services.GetRequiredService<ChogLifeMatchRules>(), Assert.Single(services.GetServices<IMatchRules>()));
    }
}
