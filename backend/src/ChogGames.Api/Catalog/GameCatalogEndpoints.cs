using ChogGames.Core.Games;
using Microsoft.Extensions.Options;

namespace ChogGames.Api.Catalog;

public static class GameCatalogEndpoints
{
    public static IServiceCollection AddGameCatalog(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IValidateOptions<GameCatalogOptions>, GameCatalogOptionsValidator>();
        services.AddOptions<GameCatalogOptions>()
            .Bind(configuration.GetSection(GameCatalogOptions.SectionName))
            .ValidateOnStart();

        return services;
    }

    /// <summary><c>GET /api/games</c>: the public catalogue in configured order (SUITE-001).</summary>
    public static IEndpointRouteBuilder MapGameCatalogEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/games", (IOptionsSnapshot<GameCatalogOptions> options) =>
                options.Value.Items.Select(ToCatalogItem).ToArray())
            .WithName("GetGames");

        return endpoints;
    }

    private static GameCatalogItem ToCatalogItem(GameCatalogEntryOptions entry)
    {
        GameCatalogOptionsValidator.TryParseStatus(entry.Status, out var status);
        return new GameCatalogItem(entry.Id, entry.Name, entry.Description, status);
    }
}
