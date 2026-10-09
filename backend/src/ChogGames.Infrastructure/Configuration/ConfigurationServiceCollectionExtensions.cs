using ChogGames.Infrastructure.Health;
using ChogGames.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ChogGames.Infrastructure.Configuration;

public static class ConfigurationServiceCollectionExtensions
{
    /// <summary>Health-check tag for dependencies that must be available before serving traffic.</summary>
    public const string ReadinessTag = "ready";

    private static readonly TimeSpan DatabaseHealthCheckTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Binds and validates infrastructure settings. Validation runs when the host starts, so
    /// missing or invalid settings stop the application before it serves requests.
    /// </summary>
    public static IServiceCollection AddInfrastructureOptions(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IValidateOptions<DatabaseOptions>, DatabaseOptionsValidator>();
        services.AddOptions<DatabaseOptions>()
            .Bind(configuration.GetSection(DatabaseOptions.SectionName))
            .ValidateOnStart();

        return services;
    }

    /// <summary>Registers validated settings, the PostgreSQL DbContext and its readiness check.</summary>
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddInfrastructureOptions(configuration);

        services.AddDbContext<ChogGamesDbContext>((provider, options) =>
            options.UseNpgsql(provider.GetRequiredService<IOptions<DatabaseOptions>>().Value.ConnectionString));

        services.AddHealthChecks()
            .AddCheck<DatabaseHealthCheck>(DatabaseHealthCheck.Name, tags: [ReadinessTag], timeout: DatabaseHealthCheckTimeout);

        return services;
    }
}
