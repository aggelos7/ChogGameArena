using System.Net;
using ChogGames.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace ChogGames.Api.Tests.Health;

/// <summary>Each test uses its own freshly created, empty database, dropped afterwards.</summary>
public sealed class PostgresIntegrationTests : IAsyncLifetime
{
    private readonly string? _serverConnectionString = Environment.GetEnvironmentVariable(PostgresFactAttribute.EnvironmentVariable);
    private readonly string _databaseName = $"chog_test_{Guid.NewGuid():N}";
    private string _databaseConnectionString = string.Empty;

    public async Task InitializeAsync()
    {
        if (string.IsNullOrWhiteSpace(_serverConnectionString))
        {
            return;
        }

        _databaseConnectionString = new NpgsqlConnectionStringBuilder(_serverConnectionString) { Database = _databaseName }.ConnectionString;
        await ExecuteOnServerAsync($"CREATE DATABASE \"{_databaseName}\"");
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

    [PostgresFact]
    public async Task Migrations_apply_to_an_empty_database()
    {
        await using var factory = new ApiFactory(_databaseConnectionString);
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ChogGamesDbContext>();

        Assert.NotEmpty(await dbContext.Database.GetPendingMigrationsAsync());

        await dbContext.Database.MigrateAsync();

        Assert.Empty(await dbContext.Database.GetPendingMigrationsAsync());
        Assert.Equal(dbContext.Database.GetMigrations(), await dbContext.Database.GetAppliedMigrationsAsync());
        Assert.Contains(dbContext.Database.GetMigrations(), id => id.EndsWith("_InitialCreate", StringComparison.Ordinal));
    }

    [PostgresFact]
    public async Task Readiness_is_healthy_when_database_is_available()
    {
        await using var factory = new ApiFactory(_databaseConnectionString);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"status\":\"Healthy\"", await response.Content.ReadAsStringAsync());
    }
}
