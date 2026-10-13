using ChogGames.Games.ChogLife.Rules;
using Microsoft.Extensions.DependencyInjection;

namespace ChogGames.Api.Tests.ChogLife;

/// <summary>STORY-035, DEC-037: the API serves the current, checked Chog Life rules.</summary>
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
}
