using ChogGames.Api.ChogRoyale;
using ChogGames.Core.Abilities;
using ChogGames.Core.Matches;
using ChogGames.Games.ChogRoyale;
using ChogGames.Games.ChogRoyale.Abilities;
using ChogGames.Games.ChogRoyale.Rules;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ChogGames.Api.Tests.ChogRoyale;

/// <summary>
/// STORY-071, DEC-082: the API serves the checked Chog Royale rules and plays them in the match engine's round mode beside
/// the other games, with its ability catalog (STORY-080, DEC-091).
/// </summary>
public class ChogRoyaleRegistrationTests
{
    [Fact]
    public async Task The_api_registers_the_royale_rules_as_a_round_game()
    {
        await using var factory = new ApiFactory(ApiFactory.UnusedDatabase);

        Assert.Equal([RoyaleRulesV1.Rules, RoyaleRulesV2.Rules], factory.Services.GetServices<RoyaleRules>());
        var rules = Assert.IsAssignableFrom<IRoundMatchRules>(Assert.Single(factory.Services.GetServices<IMatchRules>(), r => r.GameType == "chog-royale"));
        Assert.Equal((TimeSpan.FromSeconds(25), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(20)), (rules.RoundTiming.Decision, rules.RoundTiming.FirstOpensAfter, rules.RoundTiming.OpensAfter));
        Assert.Equal(TimeSpan.FromMinutes(5), rules.TimeLimit);
    }

    [Fact]
    public void Chog_royale_registers_its_match_rules_and_abilities()
    {
        using var services = new ServiceCollection().AddChogRoyale().BuildServiceProvider();

        Assert.Same(services.GetRequiredService<ChogRoyaleMatchRules>(), Assert.Single(services.GetServices<IMatchRules>()));
        var catalog = Assert.Single(services.GetServices<AbilityCatalog>());
        Assert.Equal(("chog-royale", RoyaleAbilities.Version, 6), (catalog.GameType, catalog.Version, catalog.Definitions.Count));
    }

    [Fact]
    public void Its_catalogue_status_defaults_to_coming_soon()
    {
        var configuration = new ConfigurationBuilder().AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json")).Build();

        Assert.Equal(("chog-royale", "ComingSoon"), (configuration["Games:Items:2:Id"], configuration["Games:Items:2:Status"]));
    }
}
