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

    private async Task<RoomResult> Create(Player host, string gameType = GameDefinition.ChogLife)
    {
        await using var db = database.CreateDbContext();
        return await Service(db).CreateAsync(host, gameType, CancellationToken.None);
    }

    private async Task<RoomResult> Join(Player player, string code)
    {
        Assert.True(RoomCode.TryParse(code, out var roomCode));
        await using var db = database.CreateDbContext();
        return await Service(db).JoinAsync(player, roomCode, CancellationToken.None);
    }

    private async Task<(GameRoom Room, Player Host)> RoomWithHost()
    {
        var host = await SavedGuest();
        var created = await Create(host);
        Assert.Equal(RoomOutcome.Created, created.Outcome);
        return (created.Room!, host);
    }

    private async Task<GameRoom> Stored(GameRoom room)
    {
        await using var db = database.CreateDbContext();
        return await db.GameRooms.AsNoTracking().SingleAsync(r => r.Id == room.Id);
    }

    private async Task SetStatus(GameRoom room, RoomStatus status)
    {
        await using var db = database.CreateDbContext();
        await RoomStatusOverride.SetAsync(db, room.Code, status, clock.GetUtcNow());
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

        Assert.Equal(RoomOutcome.Created, result.Outcome);
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

        Assert.Equal((RoomOutcome.UnknownGame, null), (result.Outcome, result.Room));
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

        Assert.Equal((RoomOutcome.GameUnavailable, null), (result.Outcome, result.Room));
        Assert.Empty(await RoomsHostedBy(host));
    }

    [PostgresFact]
    public async Task Host_with_a_waiting_room_cannot_open_another()
    {
        var host = await SavedGuest();
        Assert.Equal(RoomOutcome.Created, (await Create(host)).Outcome);

        var second = await Create(host, GameDefinition.ChogRace);

        Assert.Equal((RoomOutcome.AlreadyInRoom, null), (second.Outcome, second.Room));
        Assert.Equal(Assert.Single(await RoomsHostedBy(host)).Code, second.OpenRoomCode);
    }

    [PostgresTheory]
    [InlineData(RoomStatus.Completed)]
    [InlineData(RoomStatus.Cancelled)]
    [InlineData(RoomStatus.Abandoned)]
    public async Task Host_whose_room_has_ended_can_open_another(RoomStatus status)
    {
        var host = await SavedGuest();
        var first = (await Create(host)).Room!;
        await SetStatus(first, status);

        var second = await Create(host);

        Assert.Equal(RoomOutcome.Created, second.Outcome);
        Assert.Equal(2, (await RoomsHostedBy(host)).Count);
    }

    [PostgresFact]
    public async Task A_started_match_blocks_creating_and_joining_other_rooms()
    {
        var (room, host) = await RoomWithHost();
        var bob = await SavedGuest();
        await Join(bob, room.Code);
        await SetStatus(room, RoomStatus.Active);
        var (other, _) = await RoomWithHost();

        var created = await Create(host);
        var joined = await Join(bob, other.Code);

        Assert.Equal((RoomOutcome.AlreadyInRoom, room.Code), (created.Outcome, created.OpenRoomCode));
        Assert.Equal((RoomOutcome.AlreadyInRoom, room.Code), (joined.Outcome, joined.OpenRoomCode));
        Assert.Single(await RoomsHostedBy(host));
        Assert.Single((await Stored(other)).Members);
    }

    [PostgresFact]
    public async Task A_player_who_left_a_started_match_is_free_but_cannot_rejoin_it()
    {
        var (room, host) = await RoomWithHost();
        var bob = await SavedGuest();
        await Join(bob, room.Code);
        await SetStatus(room, RoomStatus.Active);
        await using (var db = database.CreateDbContext())
        {
            const string character = """
                {"kind":"Standard","id":"classic","name":"Classic","imageUrl":null,"traits":[],"walletAddress":null,"verifiedAt":"2026-10-11T12:00:00+00:00"}
                """;
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE "RoomMembers" SET "Character" = {character}::jsonb, "LeftAt" = {clock.GetUtcNow()}
                WHERE "RoomId" = {room.Id} AND "PlayerId" = {bob.Id}
                """);
        }

        Assert.Equal(RoomOutcome.RoomStarted, (await Join(bob, room.Code)).Outcome);
        Assert.Equal(RoomOutcome.Created, (await Create(bob)).Outcome);
        Assert.Equal(RoomOutcome.AlreadyInRoom, (await Create(host)).Outcome);
    }

    [PostgresFact]
    public async Task Concurrent_creates_by_the_same_host_open_one_room()
    {
        var host = await SavedGuest();

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => Create(host))));

        Assert.Single(results, r => r.Outcome == RoomOutcome.Created);
        Assert.Equal(3, results.Count(r => r.Outcome == RoomOutcome.AlreadyInRoom));
        Assert.Single(await RoomsHostedBy(host));
    }

    [PostgresFact]
    public async Task Member_of_another_waiting_room_cannot_create_a_room()
    {
        var (room, _) = await RoomWithHost();
        var bob = await SavedGuest();
        Assert.Equal(RoomOutcome.Joined, (await Join(bob, room.Code)).Outcome);

        var result = await Create(bob);

        Assert.Equal((RoomOutcome.AlreadyInRoom, room.Code), (result.Outcome, result.OpenRoomCode));
        Assert.Empty(await RoomsHostedBy(bob));
    }

    [PostgresFact]
    public async Task Second_player_joins_a_waiting_room()
    {
        var (room, host) = await RoomWithHost();
        var bob = await SavedGuest();
        clock.Advance(TimeSpan.FromMinutes(1));

        var result = await Join(bob, room.Code.ToLowerInvariant());

        Assert.Equal(RoomOutcome.Joined, result.Outcome);
        var stored = await Stored(room);
        Assert.Equal([host.Id, bob.Id], stored.Members.OrderBy(m => m.JoinedAt).Select(m => m.PlayerId));
        Assert.Equal(clock.GetUtcNow(), stored.Members.Single(m => m.PlayerId == bob.Id).JoinedAt);
        Assert.Equal(host.Id, stored.HostPlayerId);
    }

    [PostgresFact]
    public async Task Joining_again_changes_nothing()
    {
        var (room, host) = await RoomWithHost();
        var bob = await SavedGuest();
        await Join(bob, room.Code);

        var again = await Join(bob, room.Code);
        var hostJoins = await Join(host, room.Code);

        Assert.Equal((RoomOutcome.AlreadyMember, RoomOutcome.AlreadyMember), (again.Outcome, hostJoins.Outcome));
        Assert.Equal(2, (await Stored(room)).Members.Count);
    }

    [PostgresFact]
    public async Task Unknown_code_is_not_found()
    {
        var bob = await SavedGuest();

        var result = await Join(bob, codes.Fresh());

        Assert.Equal((RoomOutcome.RoomNotFound, null), (result.Outcome, result.Room));
    }

    [PostgresFact]
    public async Task Seventh_player_is_refused_when_six_are_in()
    {
        var (room, _) = await RoomWithHost();
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(RoomOutcome.Joined, (await Join(await SavedGuest(), room.Code)).Outcome);
        }

        var result = await Join(await SavedGuest(), room.Code);

        Assert.Equal(RoomOutcome.RoomFull, result.Outcome);
        Assert.Equal(6, (await Stored(room)).Members.Count);
    }

    [PostgresTheory]
    [InlineData(RoomStatus.Active, RoomOutcome.RoomStarted)]
    [InlineData(RoomStatus.Completed, RoomOutcome.RoomClosed)]
    [InlineData(RoomStatus.Cancelled, RoomOutcome.RoomClosed)]
    [InlineData(RoomStatus.Abandoned, RoomOutcome.RoomClosed)]
    public async Task Room_that_is_no_longer_waiting_cannot_be_joined(RoomStatus status, RoomOutcome expected)
    {
        var (room, _) = await RoomWithHost();
        var bob = await SavedGuest();
        await SetStatus(room, status);

        var result = await Join(bob, room.Code);

        Assert.Equal(expected, result.Outcome);
        Assert.Single((await Stored(room)).Members);
    }

    [PostgresFact]
    public async Task Member_reopening_a_started_room_gets_the_room()
    {
        var (room, _) = await RoomWithHost();
        var bob = await SavedGuest();
        await Join(bob, room.Code);
        await SetStatus(room, RoomStatus.Active);

        var result = await Join(bob, room.Code);

        Assert.Equal((RoomOutcome.AlreadyMember, room.Id), (result.Outcome, result.Room!.Id));
    }

    [PostgresFact]
    public async Task Player_in_another_waiting_room_cannot_join()
    {
        var (first, _) = await RoomWithHost();
        var (second, otherHost) = await RoomWithHost();
        var bob = await SavedGuest();
        await Join(bob, first.Code);

        var asMember = await Join(bob, second.Code);
        var asHost = await Join(otherHost, first.Code);

        Assert.Equal((RoomOutcome.AlreadyInRoom, first.Code), (asMember.Outcome, asMember.OpenRoomCode));
        Assert.Equal((RoomOutcome.AlreadyInRoom, second.Code), (asHost.Outcome, asHost.OpenRoomCode));
        Assert.Single((await Stored(second)).Members);
    }

    [PostgresFact]
    public async Task Concurrent_joins_never_overfill_a_room()
    {
        var (room, _) = await RoomWithHost();
        for (var i = 0; i < 4; i++)
        {
            await Join(await SavedGuest(), room.Code);
        }

        var joiners = new List<Player>();
        for (var i = 0; i < 4; i++)
        {
            joiners.Add(await SavedGuest());
        }

        var results = await Task.WhenAll(joiners.Select(p => Task.Run(() => Join(p, room.Code))));

        Assert.Single(results, r => r.Outcome == RoomOutcome.Joined);
        Assert.Equal(3, results.Count(r => r.Outcome == RoomOutcome.RoomFull));
        Assert.Equal(6, (await Stored(room)).Members.Count);
    }

    [PostgresFact]
    public async Task Concurrent_joins_by_one_player_enter_one_room()
    {
        var rooms = new List<GameRoom>();
        for (var i = 0; i < 3; i++)
        {
            rooms.Add((await RoomWithHost()).Room);
        }

        var bob = await SavedGuest();

        var results = await Task.WhenAll(rooms.Select(r => Task.Run(() => Join(bob, r.Code))));

        Assert.Single(results, r => r.Outcome == RoomOutcome.Joined);
        Assert.Equal(2, results.Count(r => r.Outcome == RoomOutcome.AlreadyInRoom));
        var memberships = 0;
        foreach (var r in rooms)
        {
            memberships += (await Stored(r)).Members.Count(m => m.PlayerId == bob.Id);
        }

        Assert.Equal(1, memberships);
    }

    [PostgresFact]
    public async Task Only_members_can_read_a_room()
    {
        var (room, host) = await RoomWithHost();
        var bob = await SavedGuest();
        Assert.True(RoomCode.TryParse(room.Code, out var code));
        await using var db = database.CreateDbContext();

        Assert.Equal(room.Id, (await Service(db).FindForMemberAsync(host, code, CancellationToken.None))?.Id);
        Assert.Null(await Service(db).FindForMemberAsync(bob, code, CancellationToken.None));
    }

    [PostgresFact]
    public async Task Code_collision_draws_another_code()
    {
        var alice = await SavedGuest();
        var bob = await SavedGuest();
        var taken = codes.Fresh();
        codes.Queue.Enqueue(taken);
        Assert.Equal(RoomOutcome.Created, (await Create(alice)).Outcome);
        codes.Queue.Enqueue(taken);

        var result = await Create(bob);

        Assert.Equal(RoomOutcome.Created, result.Outcome);
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
        Assert.Equal(RoomOutcome.Created, (await Create(alice)).Outcome);
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
            INSERT INTO "GameRooms" ("Id", "Code", "GameType", "HostPlayerId", "Status", "Capacity", "MinPlayers", "CreatedAt")
            VALUES ({Guid.NewGuid()}, {code}, {gameType}, {host.Id}, {status}, {capacity}, 2, {clock.GetUtcNow()})
            """));

        Assert.Equal(sqlState, error.SqlState);
    }

    [PostgresTheory]
    [InlineData(1, 6, "Waiting", false)] // check: minimum below two
    [InlineData(4, 3, "Waiting", false)] // check: minimum above capacity
    [InlineData(2, 6, "Waiting", true)] // check: a waiting room has not started
    public async Task Database_rejects_invalid_lobby_settings(int minPlayers, int capacity, string status, bool started)
    {
        var host = await SavedGuest();
        await using var db = database.CreateDbContext();
        DateTimeOffset? startedAt = started ? clock.GetUtcNow() : null;

        var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "GameRooms" ("Id", "Code", "GameType", "HostPlayerId", "Status", "Capacity", "MinPlayers", "CreatedAt", "StartedAt")
            VALUES ({Guid.NewGuid()}, 'K7QF3M', 'chog-life', {host.Id}, {status}, {capacity}, {minPlayers}, {clock.GetUtcNow()}, {startedAt})
            """));

        Assert.Equal("23514", error.SqlState);
    }

    [PostgresTheory]
    [InlineData("Waiting", true, false, "CK_GameRooms_StartedAt")] // waiting rooms have not started
    [InlineData("Cancelled", true, true, "CK_GameRooms_StartedAt")] // only a waiting room is cancelled
    [InlineData("Active", false, false, "CK_GameRooms_StartedAt")] // a match has a start time
    [InlineData("Completed", false, true, "CK_GameRooms_StartedAt")]
    [InlineData("Abandoned", false, true, "CK_GameRooms_StartedAt")]
    [InlineData("Waiting", false, true, "CK_GameRooms_EndedAt")] // open rooms have not ended
    [InlineData("Active", true, true, "CK_GameRooms_EndedAt")]
    [InlineData("Cancelled", false, false, "CK_GameRooms_EndedAt")] // final rooms have an end time
    [InlineData("Completed", true, false, "CK_GameRooms_EndedAt")]
    [InlineData("Abandoned", true, false, "CK_GameRooms_EndedAt")]
    public async Task Database_rejects_lifecycle_times_that_do_not_match_the_status(string status, bool started, bool ended, string constraint)
    {
        var (room, _) = await RoomWithHost();
        await using var db = database.CreateDbContext();
        DateTimeOffset? startedAt = started ? clock.GetUtcNow() : null;
        DateTimeOffset? endedAt = ended ? clock.GetUtcNow() : null;

        var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "GameRooms" SET "Status" = {status}, "StartedAt" = {startedAt}, "EndedAt" = {endedAt},
                -- Turn columns that fit the status and start (DEC-027), so only the lifecycle times are under test.
                "TurnTimeout" = CASE WHEN {started} THEN interval '20 seconds' END,
                "TurnNumber" = CASE WHEN {started} THEN 1 ELSE 0 END,
                "CurrentPlayerId" = CASE WHEN {status} = 'Active' THEN "HostPlayerId" END,
                "TurnEndsAt" = CASE WHEN {status} = 'Active' THEN now() END
            WHERE "Id" = {room.Id}
            """));

        Assert.Equal(("23514", constraint), (error.SqlState, error.ConstraintName));
    }

    [PostgresTheory]
    [InlineData("Waiting", false, false)]
    [InlineData("Active", true, false)]
    [InlineData("Completed", true, true)]
    [InlineData("Cancelled", false, true)]
    [InlineData("Abandoned", true, true)]
    public async Task Database_accepts_each_status_with_its_lifecycle_times(string status, bool started, bool ended)
    {
        var (room, _) = await RoomWithHost();
        await using var db = database.CreateDbContext();
        DateTimeOffset? startedAt = started ? clock.GetUtcNow() : null;
        DateTimeOffset? endedAt = ended ? clock.GetUtcNow() : null;

        Assert.Equal(1, await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "GameRooms" SET "Status" = {status}, "StartedAt" = {startedAt}, "EndedAt" = {endedAt},
                -- Turn columns that fit the status and start (DEC-027), so only the lifecycle times are under test.
                "TurnTimeout" = CASE WHEN {started} THEN interval '20 seconds' END,
                "TurnNumber" = CASE WHEN {started} THEN 1 ELSE 0 END,
                "CurrentPlayerId" = CASE WHEN {status} = 'Active' THEN "HostPlayerId" END,
                "TurnEndsAt" = CASE WHEN {status} = 'Active' THEN now() END
            WHERE "Id" = {room.Id}
            """));
    }

    [PostgresTheory]
    [InlineData("[]")] // check: the snapshot is a JSON object
    [InlineData("\"mint\"")]
    [InlineData("null")]
    public async Task Database_rejects_a_snapshot_that_is_not_an_object(string character)
    {
        var (room, host) = await RoomWithHost();
        await using var db = database.CreateDbContext();

        var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "RoomMembers" SET "Character" = {character}::jsonb
            WHERE "RoomId" = {room.Id} AND "PlayerId" = {host.Id}
            """));

        Assert.Equal(("23514", "CK_RoomMembers_Character"), (error.SqlState, error.ConstraintName));
    }

    [PostgresTheory]
    [InlineData("")] // check: a chosen ability is an ability id (DEC-035)
    [InlineData("Reward_Boost")]
    [InlineData("reward boost")]
    [InlineData("1boost")]
    public async Task Database_rejects_a_malformed_ability_choice(string abilityId)
    {
        var (room, host) = await RoomWithHost();
        await using var db = database.CreateDbContext();

        var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "RoomMembers" SET "AbilityId" = {abilityId}
            WHERE "RoomId" = {room.Id} AND "PlayerId" = {host.Id}
            """));

        Assert.Equal(("23514", "CK_RoomMembers_AbilityId"), (error.SqlState, error.ConstraintName));
    }

    [PostgresTheory]
    [InlineData("reward_boost", "{\"id\":\"reward_boost\"}", false)] // check: locked only at start, with the character
    [InlineData(null, "{\"id\":\"reward_boost\"}", true)] // check: only a chosen ability is locked
    [InlineData("reward_boost", "{\"id\":\"event_reroll\"}", true)] // check: the locked one is the chosen one
    [InlineData("reward_boost", "{\"name\":\"Reward Boost\"}", true)] // check: a null id is not a match
    [InlineData("reward_boost", "[\"reward_boost\"]", true)] // check: a JSON object
    [InlineData("reward_boost", "null", true)]
    public async Task Database_rejects_an_inconsistent_locked_ability(string? abilityId, string ability, bool withCharacter)
    {
        var (room, host) = await RoomWithHost();
        await using var db = database.CreateDbContext();
        string? character = withCharacter ? "{\"kind\":\"Standard\"}" : null;

        var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "RoomMembers" SET "AbilityId" = {abilityId}, "Ability" = {ability}::jsonb, "Character" = {character}::jsonb
            WHERE "RoomId" = {room.Id} AND "PlayerId" = {host.Id}
            """));

        Assert.Equal(("23514", "CK_RoomMembers_Ability"), (error.SqlState, error.ConstraintName));
    }

    [PostgresFact]
    public async Task Database_accepts_a_locked_ability_that_matches_the_choice()
    {
        var (room, host) = await RoomWithHost();
        await using var db = database.CreateDbContext();

        Assert.Equal(1, await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "RoomMembers" SET "AbilityId" = 'reward_boost', "Ability" = {"{\"id\":\"reward_boost\"}"}::jsonb,
                "Character" = {"{\"kind\":\"Standard\"}"}::jsonb
            WHERE "RoomId" = {room.Id} AND "PlayerId" = {host.Id}
            """));
    }

    [PostgresTheory]
    [InlineData("Standard", null)] // check: ready needs both the kind and the id
    [InlineData(null, "mint")]
    [InlineData("Shiny", "mint")] // check: kind
    public async Task Database_rejects_invalid_ready_state(string? kind, string? characterId)
    {
        var (room, host) = await RoomWithHost();
        await using var db = database.CreateDbContext();

        var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "RoomMembers" SET "ReadyCharacterKind" = {kind}, "ReadyCharacterId" = {characterId}
            WHERE "RoomId" = {room.Id} AND "PlayerId" = {host.Id}
            """));

        Assert.Equal(("23514", "CK_RoomMembers_Ready"), (error.SqlState, error.ConstraintName));
    }

    [PostgresFact]
    public async Task Database_rejects_a_duplicate_code()
    {
        var alice = await SavedGuest();
        var bob = await SavedGuest();
        var code = (await Create(alice)).Room!.Code;
        await using var db = database.CreateDbContext();

        var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "GameRooms" ("Id", "Code", "GameType", "HostPlayerId", "Status", "Capacity", "MinPlayers", "CreatedAt")
            VALUES ({Guid.NewGuid()}, {code}, 'chog-life', {bob.Id}, 'Waiting', 6, 2, {clock.GetUtcNow()})
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

        public string Fresh() => random.Next().Value;

        public RoomCode Next()
        {
            Assert.True(RoomCode.TryParse(Queue.TryDequeue(out var queued) ? queued : Fresh(), out var code));
            Issued.Add(code.Value);
            return code;
        }
    }
}
