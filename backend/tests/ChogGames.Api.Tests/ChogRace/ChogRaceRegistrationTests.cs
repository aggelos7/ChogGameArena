using ChogGames.Api.ChogRace;
using ChogGames.Core.Abilities;
using ChogGames.Core.Matches;
using ChogGames.Games.ChogRace;
using ChogGames.Games.ChogRace.Rules;
using Microsoft.Extensions.DependencyInjection;

namespace ChogGames.Api.Tests.ChogRace;

/// <summary>
/// STORY-050, DEC-054: the API serves the checked Chog Race rules and plays them in the match engine next to Chog Life,
/// with no abilities yet (STORY-055).
/// </summary>
public class ChogRaceRegistrationTests
{
    [Fact]
    public async Task The_api_registers_the_race_rules_beside_chog_life()
    {
        await using var factory = new ApiFactory(ApiFactory.UnusedDatabase);

        Assert.Same(RaceRulesV1.Rules, Assert.Single(factory.Services.GetServices<RaceRules>()));
        Assert.Equal(
            ["chog-life", "chog-race"],
            factory.Services.GetServices<IMatchRules>().Select(rules => rules.GameType).Order());
    }

    [Fact]
    public void Chog_race_registers_its_match_rules_and_no_abilities()
    {
        using var services = new ServiceCollection().AddChogRace().BuildServiceProvider();

        Assert.Same(services.GetRequiredService<ChogRaceMatchRules>(), Assert.Single(services.GetServices<IMatchRules>()));
        Assert.Empty(services.GetServices<AbilityCatalog>());
    }
}
