using ChogGames.Core.Games;
using ChogGames.Core.Matches;
using ChogGames.Core.Players;
using ChogGames.Core.Rooms;
using ChogGames.Infrastructure.Identity;
using ChogGames.Infrastructure.Nfts.Cache;
using ChogGames.Infrastructure.Wallets;
using Microsoft.EntityFrameworkCore;

namespace ChogGames.Infrastructure.Persistence;

/// <summary>
/// PostgreSQL system of record (ADR-010). Entities are added by the stories that own them;
/// schema changes are applied only through reviewed EF Core migrations.
/// </summary>
public sealed class ChogGamesDbContext(DbContextOptions<ChogGamesDbContext> options) : DbContext(options)
{
    public DbSet<Player> Players => Set<Player>();

    public DbSet<GuestSession> GuestSessions => Set<GuestSession>();

    public DbSet<WalletChallenge> WalletChallenges => Set<WalletChallenge>();

    public DbSet<ChogNft> ChogNfts => Set<ChogNft>();

    public DbSet<NftOwnershipScan> NftOwnershipScans => Set<NftOwnershipScan>();

    public DbSet<GameDefinition> GameDefinitions => Set<GameDefinition>();

    public DbSet<GameRoom> GameRooms => Set<GameRoom>();

    public DbSet<MatchAction> MatchActions => Set<MatchAction>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ChogGamesDbContext).Assembly);
    }
}
