namespace ChogGames.Api.Tests;

/// <summary>Theory counterpart of <see cref="PostgresFactAttribute"/>.</summary>
public sealed class PostgresTheoryAttribute : TheoryAttribute
{
    public PostgresTheoryAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(PostgresFactAttribute.EnvironmentVariable)))
        {
            Skip = $"Set {PostgresFactAttribute.EnvironmentVariable} to run PostgreSQL integration tests.";
        }
    }
}
