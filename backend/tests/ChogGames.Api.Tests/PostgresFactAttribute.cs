namespace ChogGames.Api.Tests;

/// <summary>
/// Runs only when <c>CHOG_TEST_POSTGRES</c> holds a connection string for a PostgreSQL server
/// whose user may create databases (e.g. the local Docker Compose instance).
/// </summary>
public sealed class PostgresFactAttribute : FactAttribute
{
    public const string EnvironmentVariable = "CHOG_TEST_POSTGRES";

    public PostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(EnvironmentVariable)))
        {
            Skip = $"Set {EnvironmentVariable} to run PostgreSQL integration tests.";
        }
    }
}
