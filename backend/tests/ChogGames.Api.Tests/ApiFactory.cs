using ChogGames.Core.Abilities;
using ChogGames.Core.Matches;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ChogGames.Api.Tests;

/// <summary>
/// Hosts the API in-memory with an explicit database connection string and optional extra settings. Chance is scripted
/// (STORY-029): matches seat players in join order, host first, unless <paramref name="configureServices"/> registers
/// another <see cref="IRandomSource"/>. No game offers abilities (STORY-042, STORY-055): Chog Life's and Chog Race's
/// catalogs are taken out, so their players ready without one; tests of abilities register a catalog again, such as with
/// <c>AddChogLifeAbilities()</c> or <c>AddChogRaceAbilities()</c>.
/// </summary>
public sealed class ApiFactory(
    string connectionString,
    IReadOnlyDictionary<string, string?>? settings = null,
    Action<IServiceCollection>? configureServices = null)
    : WebApplicationFactory<Program>
{
    /// <summary>A syntactically valid connection string for tests that never open a connection.</summary>
    public const string UnusedDatabase = "Host=127.0.0.1;Port=1;Database=choggames;Username=choggames;Timeout=2";

    public const string WalletAuthDomain = "arena.example.test";
    public const string WalletAuthUri = "https://arena.example.test";

    /// <summary>HTTPS client so Secure cookies round-trip exactly as in production.</summary>
    public HttpClient CreateHttpsClient() =>
        CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Not "Development", so local user-secrets and development overrides are never loaded into tests.
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration(config =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:ConnectionString"] = connectionString,
                ["WalletAuth:Domain"] = WalletAuthDomain,
                ["WalletAuth:Uri"] = WalletAuthUri,
            });
            if (settings is not null)
            {
                config.AddInMemoryCollection(settings);
            }
        });

        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IRandomSource>(ScriptedRandomSource.KeepOrder);
            services.RemoveAll<AbilityCatalog>();
        });
        if (configureServices is not null)
        {
            builder.ConfigureTestServices(configureServices);
        }
    }
}
