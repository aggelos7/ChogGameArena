using ChogGames.Core.Matches;
using ChogGames.Games.ChogLife;
using ChogGames.Games.ChogLife.Rules;

namespace ChogGames.Api.ChogLife;

public static class ChogLifeServiceCollectionExtensions
{
    /// <summary>
    /// Registers the current Chog Life rules (STORY-035, DEC-037) and plays them in the match engine (STORY-036,
    /// DEC-038). Building them here checks the board, so a malformed one stops the API at startup. Every rules version
    /// registered stays playable for matches that started with it.
    /// </summary>
    public static IServiceCollection AddChogLife(this IServiceCollection services)
    {
        services.AddSingleton(LifeRulesV1.Rules);
        services.AddSingleton<IMatchRules>(sp => new ChogLifeMatchRules(sp.GetServices<LifeRules>()));
        return services;
    }
}
