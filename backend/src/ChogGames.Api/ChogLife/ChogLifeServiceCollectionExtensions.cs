using ChogGames.Games.ChogLife.Rules;

namespace ChogGames.Api.ChogLife;

public static class ChogLifeServiceCollectionExtensions
{
    /// <summary>
    /// Registers the current Chog Life rules (STORY-035, DEC-037). Building them here checks the board, so a malformed
    /// one stops the API at startup.
    /// </summary>
    public static IServiceCollection AddChogLife(this IServiceCollection services)
    {
        services.AddSingleton(LifeRulesV1.Rules);
        return services;
    }
}
