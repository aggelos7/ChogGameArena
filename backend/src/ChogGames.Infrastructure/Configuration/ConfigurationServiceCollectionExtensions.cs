using ChogGames.Core.Abilities;
using ChogGames.Infrastructure.Abilities;
using ChogGames.Infrastructure.Characters;
using ChogGames.Infrastructure.Health;
using ChogGames.Core.Matches;
using ChogGames.Core.Nfts;
using ChogGames.Core.Progression;
using ChogGames.Core.Rooms;
using ChogGames.Infrastructure.Identity;
using ChogGames.Infrastructure.Matches;
using ChogGames.Infrastructure.Monitoring;
using ChogGames.Infrastructure.Nfts;
using ChogGames.Infrastructure.Nfts.Cache;
using ChogGames.Infrastructure.Nfts.Metadata;
using ChogGames.Infrastructure.Nfts.Monad;
using ChogGames.Infrastructure.Persistence;
using ChogGames.Infrastructure.Progression;
using ChogGames.Infrastructure.Rooms;
using ChogGames.Infrastructure.Wallets;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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

        // Game and session metrics (STORY-048); room status changes are counted as they are committed.
        services.AddMetrics();
        services.AddSingleton<ChogGamesMetrics>();
        services.AddSingleton<RoomLifecycleMetricsInterceptor>();
        services.AddDbContext<ChogGamesDbContext>((provider, options) => options
            .UseNpgsql(provider.GetRequiredService<IOptions<DatabaseOptions>>().Value.ConnectionString)
            .AddInterceptors(provider.GetRequiredService<RoomLifecycleMetricsInterceptor>()));

        services.AddOptions<GuestSessionOptions>()
            .Bind(configuration.GetSection(GuestSessionOptions.SectionName))
            .Validate(o => o.Lifetime > TimeSpan.Zero && o.RenewInterval > TimeSpan.Zero && o.RenewInterval < o.Lifetime,
                "GuestSessions:Lifetime and RenewInterval must be positive and RenewInterval shorter than Lifetime.")
            .Validate(o => o.CreatePerMinutePerClient > 0, "GuestSessions:CreatePerMinutePerClient must be positive.")
            .ValidateOnStart();
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<GuestSessionService>();

        services.AddSingleton<IValidateOptions<WalletAuthOptions>, WalletAuthOptionsValidator>();
        services.AddOptions<WalletAuthOptions>()
            .Bind(configuration.GetSection(WalletAuthOptions.SectionName))
            .ValidateOnStart();
        services.AddScoped<WalletSignInService>();
        services.AddScoped<CharacterEligibilityService>();
        services.AddSingleton<IRoomCodeGenerator, SecureRoomCodeGenerator>();
        services.AddScoped<RoomService>();
        services.AddScoped<LobbyService>();

        // Games add their IMatchRules with their own registration, such as AddChogLife (DEC-026, DEC-038); a game without
        // any answers every command type as unknown.
        services.AddOptions<MatchOptions>()
            .Bind(configuration.GetSection(MatchOptions.SectionName))
            .Validate(o => o.TurnTimeout >= MatchOptions.MinTurnTimeout && o.TurnTimeout <= MatchOptions.MaxTurnTimeout,
                $"Matches:TurnTimeout must be between {MatchOptions.MinTurnTimeout} and {MatchOptions.MaxTurnTimeout}.")
            .Validate(o => o.MaxMissedTurns is >= 1 and <= 20, "Matches:MaxMissedTurns must be between 1 and 20.")
            .Validate(o => o.TimeoutCheckInterval >= TimeSpan.FromMilliseconds(100) && o.TimeoutCheckInterval <= TimeSpan.FromMinutes(1),
                "Matches:TimeoutCheckInterval must be between 100 milliseconds and 1 minute.")
            .ValidateOnStart();
        services.AddOptions<ProgressionOptions>()
            .Bind(configuration.GetSection(ProgressionOptions.SectionName))
            .Validate(o => o.Xp.Version >= 1, "Progression:Xp:Version must be at least 1.")
            .Validate(o => o.Xp.PerMatch is >= 0 and <= XpRules.MaxValue && o.Xp.PerOpponentBeaten is >= 0 and <= XpRules.MaxValue,
                $"Progression:Xp:PerMatch and PerOpponentBeaten must be between 0 and {XpRules.MaxValue}.")
            .ValidateOnStart();
        services.AddSingleton<IRandomSource, SecureRandomSource>();
        services.AddScoped<MatchCommandService>();
        services.AddScoped<TurnTimeoutService>();
        services.AddScoped<MatchRecoveryService>();

        // Games add their AbilityCatalog as their abilities are built (DEC-032). Every player gets the shared pool
        // unless trait-based abilities are switched on, which replaces only this one registration (DEC-033, DEC-066).
        services.AddSingleton<AbilityEngine>();
        services.AddSingleton<IValidateOptions<TraitAbilityOptions>, TraitAbilityOptionsValidator>();
        services.AddOptions<TraitAbilityOptions>()
            .Bind(configuration.GetSection(TraitAbilityOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IAbilityAssignmentStrategy>(provider =>
            provider.GetRequiredService<IOptions<TraitAbilityOptions>>().Value is { Enabled: true } traitBased
                ? new TraitBasedAbilityStrategy(new ConfiguredTraitAbilityResolver(traitBased.ToRules()))
                : new SharedAbilityPoolStrategy());
        services.AddSingleton<AbilityPool>();
        services.AddScoped<LobbyCharacters>();

        services.AddSingleton<IValidateOptions<NftOptions>, NftOptionsValidator>();
        services.AddOptions<NftOptions>()
            .Bind(configuration.GetSection(NftOptions.SectionName))
            .ValidateOnStart();
        services.AddHttpClient(MonadRpcClient.HttpClientName, (provider, client) =>
        {
            var monad = provider.GetRequiredService<IOptions<NftOptions>>().Value.Monad;
            client.BaseAddress = new Uri(monad.RpcUrl);
            client.Timeout = monad.RequestTimeout;
        });
        services.AddHttpClient(IpfsNftMetadataSource.HttpClientName, (provider, client) =>
                client.Timeout = provider.GetRequiredService<IOptions<NftOptions>>().Value.Ipfs.RequestTimeout)
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddSingleton<MockNftProvider>();
        services.AddSingleton<MonadNftProvider>();
        services.AddSingleton<ITokenUriReader>(provider => provider.GetRequiredService<MonadNftProvider>());
        services.AddSingleton<INftMetadataSource, IpfsNftMetadataSource>();
        // Scoped: the Monad provider is wrapped by the database-backed cache (STORY-016).
        services.AddScoped<INftProvider>(provider =>
            provider.GetRequiredService<IOptions<NftOptions>>().Value.Provider switch
            {
                NftProviderKind.Mock => provider.GetRequiredService<MockNftProvider>(),
                NftProviderKind.Monad => ActivatorUtilities.CreateInstance<CachedNftProvider>(
                    provider, provider.GetRequiredService<MonadNftProvider>()),
                _ => new UnconfiguredNftProvider(),
            });

        services.AddHealthChecks()
            .AddCheck<DatabaseHealthCheck>(DatabaseHealthCheck.Name, tags: [ReadinessTag], timeout: DatabaseHealthCheckTimeout);

        return services;
    }
}
