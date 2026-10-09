namespace ChogGames.Infrastructure.Configuration;

/// <summary>PostgreSQL settings. Bound from the <c>Database</c> configuration section.</summary>
public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    /// <summary>
    /// Npgsql connection string. Contains credentials: supply it through user-secrets or the
    /// <c>Database__ConnectionString</c> environment variable, never through committed appsettings.
    /// </summary>
    public string ConnectionString { get; set; } = string.Empty;
}
