using ChogGames.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ChogGames.Api.Tests;

/// <summary>
/// xUnit class fixture: a fresh database with all migrations applied, dropped after the class.
/// Inert when <c>CHOG_TEST_POSTGRES</c> is not set (the tests using it are skipped).
/// </summary>
public sealed class MigratedPostgresDatabase : IAsyncLifetime
{
    private readonly string? _serverConnectionString = Environment.GetEnvironmentVariable(PostgresFactAttribute.EnvironmentVariable);
    private readonly string _databaseName = $"chog_test_{Guid.NewGuid():N}";

    public string ConnectionString { get; private set; } = string.Empty;

    public ChogGamesDbContext CreateDbContext() =>
        new(new DbContextOptionsBuilder<ChogGamesDbContext>().UseNpgsql(ConnectionString).Options);

    public async Task InitializeAsync()
    {
        if (string.IsNullOrWhiteSpace(_serverConnectionString))
        {
            return;
        }

        ConnectionString = new NpgsqlConnectionStringBuilder(_serverConnectionString) { Database = _databaseName }.ConnectionString;
        await ExecuteOnServerAsync($"CREATE DATABASE \"{_databaseName}\"");
        await using var dbContext = CreateDbContext();
        await dbContext.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (!string.IsNullOrWhiteSpace(_serverConnectionString))
        {
            NpgsqlConnection.ClearAllPools();
            await ExecuteOnServerAsync($"DROP DATABASE IF EXISTS \"{_databaseName}\" WITH (FORCE)");
        }
    }

    private async Task ExecuteOnServerAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(_serverConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
