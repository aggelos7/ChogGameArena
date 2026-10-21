using ChogGames.Api.ChogLife;
using ChogGames.Api.ChogRace;
using ChogGames.Api.ChogRoyale;
using ChogGames.Api.Legal;
using ChogGames.Api.Rooms;
using ChogGames.Api.Wallet;
using ChogGames.Infrastructure.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Options;

namespace ChogGames.Api.Tests;

/// <summary>
/// Runs the API's startup validation of its settings (<c>ValidateOnStart</c>) the way the host does before anything
/// starts, without starting one: a host that fails to start is disposed on another thread, which under a loaded test run
/// can surface <see cref="ObjectDisposedException"/> instead of the validation error. The settings are applied over the
/// API's appsettings.json and the ones <see cref="ApiFactory"/> gives every test, in its "Testing" environment.
/// </summary>
internal static class StartupValidation
{
    public static void Validate(IReadOnlyDictionary<string, string?>? settings = null)
    {
        var configuration = new ConfigurationBuilder()
            // The API's own appsettings.json, copied next to the tests with the API's build output.
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), optional: false)
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:ConnectionString"] = ApiFactory.UnusedDatabase,
                ["WalletAuth:Domain"] = ApiFactory.WalletAuthDomain,
                ["WalletAuth:Uri"] = ApiFactory.WalletAuthUri,
            })
            .AddInMemoryCollection(settings ?? new Dictionary<string, string?>())
            .Build();
        using var provider = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IHostEnvironment>(new HostingEnvironment { EnvironmentName = "Testing", ApplicationName = "ChogGames.Api" })
            .AddInfrastructure(configuration)
            .AddWalletChain(configuration)
            .AddRooms(configuration)
            .AddLegal(configuration)
            // With the games and their abilities, so the shipped Abilities:Holder is checked against them (DEC-077).
            .AddChogLife()
            .AddChogRace()
            .AddChogRoyale()
            .BuildServiceProvider();

        provider.GetRequiredService<IStartupValidator>().Validate();
    }

    /// <summary>The validation error <paramref name="settings"/> stop startup with.</summary>
    public static OptionsValidationException Failure(IReadOnlyDictionary<string, string?> settings) =>
        Assert.Throws<OptionsValidationException>(() => Validate(settings));
}
