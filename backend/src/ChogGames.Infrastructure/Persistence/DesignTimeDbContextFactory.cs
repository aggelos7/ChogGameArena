using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ChogGames.Infrastructure.Persistence;

/// <summary>
/// Used only by the <c>dotnet ef</c> tool. Generating migrations needs no database, so a
/// credential-free placeholder is used unless <c>Database__ConnectionString</c> is set;
/// <c>dotnet ef database update --connection "..."</c> overrides it when applying migrations.
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<ChogGamesDbContext>
{
    private const string PlaceholderConnectionString = "Host=localhost;Database=choggames;Username=choggames";

    public ChogGamesDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("Database__ConnectionString");
        var options = new DbContextOptionsBuilder<ChogGamesDbContext>()
            .UseNpgsql(string.IsNullOrWhiteSpace(connectionString) ? PlaceholderConnectionString : connectionString)
            .Options;

        return new ChogGamesDbContext(options);
    }
}
