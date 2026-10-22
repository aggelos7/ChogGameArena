using ChogGames.Core.Abilities;
using ChogGames.Core.Matches;
using ChogGames.Games.ChogRoyale;
using ChogGames.Games.ChogRoyale.Abilities;
using ChogGames.Games.ChogRoyale.Rules;

namespace ChogGames.Api.ChogRoyale;

public static class ChogRoyaleServiceCollectionExtensions
{
    /// <summary>
    /// Registers the current Chog Royale rules and plays them in the match engine, in simultaneous rounds (STORY-071,
    /// DEC-082). Building them here checks them, so invalid rules stop the API at startup. Every rules version registered
    /// stays playable for matches that started with it; new matches start with the highest. Its abilities come with it
    /// (STORY-080, DEC-091).
    /// </summary>
    public static IServiceCollection AddChogRoyale(this IServiceCollection services)
    {
        services.AddSingleton(RoyaleRulesV1.Rules);
        services.AddSingleton(RoyaleRulesV2.Rules);
        services.AddSingleton(sp => new ChogRoyaleMatchRules(sp.GetServices<RoyaleRules>()));
        services.AddSingleton<IMatchRules>(sp => sp.GetRequiredService<ChogRoyaleMatchRules>());
        return services.AddChogRoyaleAbilities();
    }

    /// <summary>Chog Royale's ability catalog, acting on the registered rules' match states (STORY-080, DEC-091).</summary>
    public static IServiceCollection AddChogRoyaleAbilities(this IServiceCollection services) =>
        services.AddSingleton<AbilityCatalog>(sp => RoyaleAbilities.Catalog(sp.GetRequiredService<ChogRoyaleMatchRules>()));
}
