using ChogGames.Core.Games;
using ChogGames.Core.Players;
using ChogGames.Core.Rooms;
using ChogGames.Infrastructure.Persistence;
using ChogGames.Infrastructure.Rooms;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace ChogGames.Api.Tests.Rooms;

public class RoomServiceTests(MigratedPostgresDatabase database) : IClassFixture<MigratedPostgresDatabase>
{
    private readonly ManualTimeProvider clock = new(new DateTimeOffset(2026, 10, 11, 12, 0, 0, TimeSpan.Zero));
    private readonly FakeCatalog catalog = new();
    private readonly FakeCodes codes = new();

    private RoomService Service(ChogGamesDbContext db) =>
        new(db, catalog, codes, clock, NullLogger<RoomService>.Instance);

    private async Task<Player> SavedGuest()
    {
        Assert.True(PlayerDisplayName.TryCreate("Alice", out var name));
        var player = Player.CreateGuest(name, clock.GetUtcNow());
        await using var db = database.CreateDbContext();
        db.Players.Add(player);
        await db.SaveChangesAsync();
        return player;
    }

    private async Task<RoomCreation> Create(Player host, string gameType = GameDefinition.ChogLife)
    {
        await using var db = database.CreateDbContext();
        return await Service(db).CreateAsync(host, gameType, CancellationToken.None);
    }

    private async Task<List<GameRoom>> RoomsHostedBy(Player host)
    {
        await using var db = database.CreateDbContext();
        return await db.GameRooms.AsNoTracking().Where(r => r.HostPlayerId == host.Id).ToListAsync();
    }

    [PostgresFact]
    public async Task Creates_a_waiting_room_for_six_with_the_host_as_member()
    {
        var host = await SavedGuest();

        var result = await Create(host);

        Assert.Equal(RoomCreationStatus.Created, result.Status);
        var stored = Assert.Single(await RoomsHostedBy(host));
        Assert.Equal(result.Room!.Id, stored.Id);
        Assert.Equal(codes.Issued[^1], stored.Code);
        Assert.Equal((GameDefinition.ChogLife, RoomStatus.Waiting, 6), (stored.GameType, stored.Status, stored.Capacity));
        Assert.Equal(clock.GetUtcNow(), stored.CreatedAt);
        Assert.Equal(host.Id, Assert.Single(stored.Members).PlayerId);
    }

    [PostgresFact]
    public async Task Both_supported_games_are_registered()
    {
        await using var db = database.CreateDbContext();

        var games = await db.GameDefinitions.AsNoTracking().OrderBy(g => g.Id).ToListAsync();

        Assert.Equal(
            [(GameDefinition.ChogLife, 2, 6), (GameDefinition.ChogRace, 2, 6)],
            games.Select(g => (g.Id, g.MinPlayers, g.MaxPlayers)));
    }

    [PostgresTheory]
    [InlineData("chog-unknown")]
    [InlineData("CHOG-LIFE")]
    [InlineData("")]
    [InlineData("chog-future")] // listed in the catalogue but without a registered definition
    public async Task Unknown_game_is_rejected_without_creating_a_room(string gameType)
    {
        var host = await SavedGuest();

        var result = await Create(host, gameType);

        Assert.Equal((RoomCreationStatus.UnknownGame, null), (result.Status, result.Room));
        Assert.Empty(await RoomsHostedBy(host));
        Assert.Empty(codes.Issued);
    }

    [PostgresTheory]
    [InlineData(GameStatus.ComingSoon)]
    [InlineData(GameStatus.Maintenance)]
    public async Task Disabled_game_is_rejected_without_creating_a_room(GameStatus status)
    {
        var host = await SavedGuest();
        catalog.Games[GameDefinition.ChogRace] = status;

        var result = await Create(host, GameDefinition.ChogRace);

        Assert.Equal((RoomCreationStatus.GameUnavailable, null), (result.Status, result.Room));
        Assert.Empty(await RoomsHostedBy(host));
    }

    [PostgresFact]
    public async Task Host_with_a_waiting_room_cannot_open_another()
    {
        var host = await SavedGuest();
        Assert.Equal(RoomCreationStatus.Created, (await Create(host)).Status);

        var second = await Create(host, GameDefinition.ChogRace);

        Assert.Equal(RoomCreationStatus.AlreadyHosting, second.Status);
        Assert.Single(await RoomsHostedBy(host));
    }

    [PostgresFact]
    public async Task Host_whose_room_is_no_longer_waiting_can_open_another()
    {
        var host = await SavedGuest();
        var first = (await Create(host)).Room!;
        await using (var db = database.CreateDbContext())
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"GameRooms\" SET \"Status\" = 'Active' WHERE \"Id\" = {first.Id}");
        }

        var second = await Create(host);

        Assert.Equal(RoomCreationStatus.Created, second.Status);
        Assert.Equal(2, (await RoomsHostedBy(host)).Count);
    }

    [PostgresFact]
    public async Task Concurrent_request_by_the_same_host_is_refused_by_the_database()
    {
        var host = await SavedGuest();
        // Another request creates this host's room after our pre-check but before our insert.
        codes.BeforeNext = () =>
        {
            codes.BeforeNext = null;
            Assert.Equal(RoomCreationStatus.Created, Create(host).GetAwaiter().GetResult().Status);
        };

        var result = await Create(host);

        Assert.Equal(RoomCreationStatus.AlreadyHosting, result.Status);
        Assert.Single(await RoomsHostedBy(host));
    }

    [PostgresFact]
    public async Task Code_collision_draws_another_code()
    {
        var alice = await SavedGuest();
        var bob = await SavedGuest();
        var taken = codes.Fresh();
        codes.Queue.Enqueue(taken);
        Assert.Equal(RoomCreationStatus.Created, (await Create(alice)).Status);
        codes.Queue.Enqueue(taken);

        var result = await Create(bob);

        Assert.Equal(RoomCreationStatus.Created, result.Status);
        Assert.NotEqual(taken, result.Room!.Code);
        Assert.Equal(result.Room.Code, Assert.Single(await RoomsHostedBy(bob)).Code);
    }

    [PostgresFact]
    public async Task Gives_up_when_every_code_drawn_is_taken_and_leaves_the_host_untouched()
    {
        var alice = await SavedGuest();
        var bob = await SavedGuest();
        var taken = codes.Fresh();
        codes.Queue.Enqueue(taken);
        Assert.Equal(RoomCreationStatus.Created, (await Create(alice)).Status);
        for (var i = 0; i < RoomService.MaxCodeAttempts; i++)
        {
            codes.Queue.Enqueue(taken);
        }

        await using var db = database.CreateDbContext();
        var trackedBob = await db.Players.SingleAsync(p => p.Id == bob.Id);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Service(db).CreateAsync(trackedBob, GameDefinition.ChogLife, CancellationToken.None));

        Assert.Contains("No unused room code", error.Message);

        Assert.Empty(await RoomsHostedBy(bob));
        Assert.Equal(EntityState.Unchanged, db.Entry(trackedBob).State);
        Assert.Empty(db.ChangeTracker.Entries<GameRoom>());
    }

    [PostgresTheory]
    [InlineData("k7qf3m", "chog-life", 6, "Waiting", "23514")] // check: code format
    [InlineData("K7QF0M", "chog-life", 6, "Waiting", "23514")]
    [InlineData("K7QF3M", "chog-life", 7, "Waiting", "23514")] // check: capacity
    [InlineData("K7QF3M", "chog-life", 1, "Waiting", "23514")]
    [InlineData("K7QF3M", "chog-life", 6, "Paused", "23514")] // check: status
    [InlineData("K7QF3M", "chog-unknown", 6, "Waiting", "23503")] // foreign key: game
    public async Task Database_rejects_invalid_rooms(string code, string gameType, int capacity, string status, string sqlState)
    {
        var host = await SavedGuest();
        await using var db = database.CreateDbContext();

        var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "GameRooms" ("Id", "Code", "GameType", "HostPlayerId", "Status", "Capacity", "CreatedAt")
            VALUES ({Guid.NewGuid()}, {code}, {gameType}, {host.Id}, {status}, {capacity}, {clock.GetUtcNow()})
            """));

        Assert.Equal(sqlState, error.SqlState);
    }

    [PostgresFact]
    public async Task Database_rejects_a_duplicate_code()
    {
        var alice = await SavedGuest();
        var bob = await SavedGuest();
        var code = (await Create(alice)).Room!.Code;
        await using var db = database.CreateDbContext();

        var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "GameRooms" ("Id", "Code", "GameType", "HostPlayerId", "Status", "Capacity", "CreatedAt")
            VALUES ({Guid.NewGuid()}, {code}, 'chog-life', {bob.Id}, 'Waiting', 6, {clock.GetUtcNow()})
            """));

        Assert.Equal(("23505", "IX_GameRooms_Code"), (error.SqlState, error.ConstraintName));
    }

    private sealed class FakeCatalog : IGameCatalog
    {
        public Dictionary<string, GameStatus> Games { get; } = new()
        {
            [GameDefinition.ChogLife] = GameStatus.Available,
            [GameDefinition.ChogRace] = GameStatus.Available,
            ["chog-future"] = GameStatus.Available,
        };

        public GameCatalogItem? Find(string gameType) =>
            Games.TryGetValue(gameType, out var status) ? new GameCatalogItem(gameType, gameType, string.Empty, status) : null;
    }

    /// <summary>Queued codes first, then secure random ones (unique across the shared test database).</summary>
    private sealed class FakeCodes : IRoomCodeGenerator
    {
        private readonly SecureRoomCodeGenerator random = new();

        public Queue<string> Queue { get; } = new();

        public List<string> Issued { get; } = [];

        public Action? BeforeNext { get; set; }

        public string Fresh() => random.Next().Value;

        public RoomCode Next()
        {
            BeforeNext?.Invoke();
            Assert.True(RoomCode.TryParse(Queue.TryDequeue(out var queued) ? queued : Fresh(), out var code));
            Issued.Add(code.Value);
            return code;
        }
    }
}
