using ChogGames.Core.Matches;
using ChogGames.Games.ChogRoyale;
using ChogGames.Games.ChogRoyale.Rules;

namespace ChogGames.Api.ChogRoyale;

public static class ChogRoyaleServiceCollectionExtensions
{
    /// <summary>
    /// Registers the current Chog Royale rules and plays them in the match engine, in simultaneous rounds (STORY-071,
    /// DEC-082). Building them here checks them, so invalid rules stop the API at startup. Every rules version registered
    /// stays playable for matches that started with it; new matches start with the highest. It has no abilities yet
    /// (STORY-080).
    /// </summary>
    public static IServiceCollection AddChogRoyale(this IServiceCollection services)
    {
        services.AddSingleton(RoyaleRulesV1.Rules);
        services.AddSingleton(sp => new ChogRoyaleMatchRules(sp.GetServices<RoyaleRules>()));
        return services.AddSingleton<IMatchRules>(sp => sp.GetRequiredService<ChogRoyaleMatchRules>());
    }
}
