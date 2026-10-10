namespace ChogGames.Api.Tests.Nfts;

/// <summary>
/// Runs only when <c>CHOG_TEST_MONAD_RPC</c> holds a Monad Mainnet JSON-RPC URL (e.g. https://rpc.monad.xyz).
/// These tests read the real chain, so they are off in CI.
/// </summary>
public sealed class MonadLiveFactAttribute : FactAttribute
{
    public const string EnvironmentVariable = "CHOG_TEST_MONAD_RPC";

    public MonadLiveFactAttribute(bool requiresPostgres = false)
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(EnvironmentVariable)))
        {
            Skip = $"Set {EnvironmentVariable} to run live Monad Mainnet tests.";
        }
        else if (requiresPostgres && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(PostgresFactAttribute.EnvironmentVariable)))
        {
            Skip = $"Set {EnvironmentVariable} and {PostgresFactAttribute.EnvironmentVariable} to run this live test.";
        }
    }
}
