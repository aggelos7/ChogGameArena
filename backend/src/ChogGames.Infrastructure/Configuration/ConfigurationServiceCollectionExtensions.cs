using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ChogGames.Infrastructure.Configuration;

public static class ConfigurationServiceCollectionExtensions
{
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
}
