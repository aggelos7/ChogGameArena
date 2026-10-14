using ChogGames.Core.Abilities;
using ChogGames.Core.Matches;
using ChogGames.Games.ChogLife;
using ChogGames.Games.ChogLife.Abilities;
using ChogGames.Games.ChogLife.Rules;

namespace ChogGames.Api.ChogLife;

public static class ChogLifeServiceCollectionExtensions
{
    /// <summary>
    /// Registers the current Chog Life rules (STORY-035, DEC-037), plays them in the match engine (STORY-036, DEC-038)
    /// and offers Chog Life's abilities (STORY-042, DEC-044). Building them here checks the board, so a malformed one
    /// stops the API at startup. Every rules version registered stays playable for matches that started with it; new
    /// matches start with the highest, version 2 since STORY-044 (DEC-047).
    /// </summary>
    public static IServiceCollection AddChogLife(this IServiceCollection services)
    {
        services.AddSingleton(LifeRulesV1.Rules);
        services.AddSingleton(LifeRulesV2.Rules);
        services.AddSingleton(sp => new ChogLifeMatchRules(sp.GetServices<LifeRules>()));
        services.AddSingleton<IMatchRules>(sp => sp.GetRequiredService<ChogLifeMatchRules>());
        return services.AddChogLifeAbilities();
    }

    /// <summary>Chog Life's ability catalog, acting on the registered rules' match states (STORY-042, DEC-044).</summary>
    public static IServiceCollection AddChogLifeAbilities(this IServiceCollection services) =>
        services.AddSingleton<AbilityCatalog>(sp => LifeAbilities.Catalog(sp.GetRequiredService<ChogLifeMatchRules>()));
}
