using ChogGames.Core.Matches;
using ChogGames.Games.ChogRace;
using ChogGames.Games.ChogRace.Rules;

namespace ChogGames.Api.ChogRace;

public static class ChogRaceServiceCollectionExtensions
{
    /// <summary>
    /// Registers the current Chog Race rules and plays them in the match engine (STORY-050, DEC-054). Building them here
    /// checks the track, so one whose finish can't be reached stops the API at startup. Every rules version registered
    /// stays playable for matches that started with it; new matches start with the highest.
    /// </summary>
    public static IServiceCollection AddChogRace(this IServiceCollection services)
    {
        services.AddSingleton(RaceRulesV1.Rules);
        services.AddSingleton(sp => new ChogRaceMatchRules(sp.GetServices<RaceRules>()));
        services.AddSingleton<IMatchRules>(sp => sp.GetRequiredService<ChogRaceMatchRules>());
        return services;
    }
}
