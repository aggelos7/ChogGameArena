using Microsoft.EntityFrameworkCore;

namespace ChogGames.Infrastructure.Persistence;

/// <summary>
/// PostgreSQL system of record (ADR-010). Entities are added by the stories that own them;
/// schema changes are applied only through reviewed EF Core migrations.
/// </summary>
public sealed class ChogGamesDbContext(DbContextOptions<ChogGamesDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ChogGamesDbContext).Assembly);
    }
}
