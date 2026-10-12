using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ChogGames.Api.Matches;
using ChogGames.Core.Matches;
using ChogGames.Core.Rooms;
using ChogGames.Infrastructure.Matches;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;

namespace ChogGames.Api.Tests.Matches;

/// <summary>Turns and the AFK timer (STORY-025, ENG-007, DEC-027).</summary>
public sealed class MatchTurnTests(MigratedPostgresDatabase database) : IClassFixture<MigratedPostgresDatabase>, IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The configured turn timeout, not the default, so the tests show the setting is used.</summary>
    private static readonly TimeSpan Turn = TimeSpan.FromSeconds(45);

    // Whole seconds, so times read back from Postgres (microseconds) compare equal.
    private readonly ManualTimeProvider _clock = new(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));

    private ApiFactory _factory = null!;

    private sealed record MemberDto(string DisplayName, bool IsYou, bool HasLeft, bool IsTurn);

    private sealed record RoomDto(
        string Code, Guid MatchId, long Version, string GameType, string Status, int TurnNumber, DateTimeOffset? TurnEndsAt, MemberDto[] Members)
    {
        public string? TurnOf => Members.SingleOrDefault(m => m.IsTurn)?.DisplayName;
    }

    private sealed record ProblemDto(int Status, string? Code);

    public async Task InitializeAsync()
    {
        if (database.ConnectionString.Length > 0)
        {
            await using var db = database.CreateDbContext();
            await db.Database.ExecuteSqlRawAsync("TRUNCATE \"GuestSessions\", \"Players\" CASCADE");
        }

        // The tests time out turns themselves, except the one that runs the scheduler (WithScheduler).
        _factory = Factory(withScheduler: false);
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    private ApiFactory Factory(bool withScheduler, IReadOnlyDictionary<string, string?>? extraSettings = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Games:Items:0:Status"] = "Available",
            ["Games:Items:1:Status"] = "Available",
            ["Matches:TurnTimeout"] = Turn.ToString(),
            ["Matches:TimeoutCheckInterval"] = "00:00:00.1",
        };
        foreach (var (key, value) in extraSettings ?? new Dictionary<string, string?>())
        {
            settings[key] = value;
        }

        // Chog Life plays the turn game below; Chog Race has no rules.
        return new ApiFactory(database.ConnectionString, settings, services =>
        {
            services.AddSingleton<TimeProvider>(_clock);
            services.AddSingleton<IMatchRules, TurnRules>();
            if (!withScheduler)
            {
                foreach (var worker in services.Where(d => d.ImplementationType == typeof(TurnTimeoutWorker)).ToList())
                {
                    services.Remove(worker);
                }
            }
        });
    }

    private async Task<HttpClient> GuestAsync(string name)
    {
        var client = _factory.CreateHttpsClient();
        using var response = await client.PostAsJsonAsync("/api/guest-sessions", new { displayName = name });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return client;
    }

    private static async Task<RoomDto> ViewAsync(HttpClient client, string code) =>
        (await client.GetFromJsonAsync<RoomDto>($"/api/rooms/{code}", Json))!;

    /// <summary>A started match of <paramref name="players"/>, the first hosting and the rest joining a second apart, in order.</summary>
    private async Task<RoomDto> StartedAsync(string gameType, params HttpClient[] players)
    {
        using var created = await players[0].PostAsJsonAsync("/api/rooms", new { gameType });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var room = (await created.Content.ReadFromJsonAsync<RoomDto>(Json))!;
        foreach (var player in players[1..])
        {
            _clock.Advance(TimeSpan.FromSeconds(1));
            (await player.PostAsync($"/api/rooms/{room.Code}/join", null)).Dispose();
        }

        foreach (var player in players)
        {
            using var ready = await player.PutAsJsonAsync($"/api/rooms/{room.Code}/ready", new { ready = true });
            Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        }

        using var started = await players[0].PostAsync($"/api/rooms/{room.Code}/start", null);
        Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        return await ViewAsync(players[0], room.Code);
    }

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, RoomDto room, string type = "ADD") =>
        client.PostAsJsonAsync(
            $"/api/rooms/{room.Code}/commands",
            new { actionId = Guid.NewGuid(), matchId = room.MatchId, expectedVersion = room.Version, type });

    private static async Task<ProblemDto> ProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        using (response)
        {
            Assert.Equal(status, response.StatusCode);
            var problem = (await response.Content.ReadFromJsonAsync<ProblemDto>(Json))!;
            Assert.Equal(((int)status, code), (problem.Status, problem.Code));
            return problem;
        }
    }

    private async Task<IReadOnlyList<TimedOutTurn>> SweepAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<TurnTimeoutService>().TimeOutOverdueTurnsAsync(CancellationToken.None);
    }

    /// <summary>The room's stored version, turn and game state.</summary>
    private async Task<(long Version, int TurnNumber, string? State)> StoredAsync(RoomDto room)
    {
        await using var db = database.CreateDbContext();
        var stored = await db.GameRooms.AsNoTracking().SingleAsync(r => r.Id == room.MatchId);
        return (stored.Version, stored.TurnNumber, stored.State is null ? null : JsonSerializer.Serialize(JsonDocument.Parse(stored.State).RootElement));
    }

    [PostgresFact]
    public async Task The_room_shows_whose_turn_it_is_and_when_it_ends()
    {
        var alice = await GuestAsync("Alice");
        var bob = await GuestAsync("Bob");
        using (alice)
        using (bob)
        {
            var room = await StartedAsync("chog-life", alice, bob);

            Assert.Equal((1, _clock.Now + Turn, "Alice"), (room.TurnNumber, room.TurnEndsAt, room.TurnOf));
            var seenByBob = await ViewAsync(bob, room.Code);
            Assert.Equal((1, _clock.Now + Turn, "Alice"), (seenByBob.TurnNumber, seenByBob.TurnEndsAt, seenByBob.TurnOf));
        }
    }

    [PostgresFact]
    public async Task A_waiting_room_has_no_turn()
    {
        using var alice = await GuestAsync("Alice");
        using var created = await alice.PostAsJsonAsync("/api/rooms", new { gameType = "chog-life" });

        var room = (await created.Content.ReadFromJsonAsync<RoomDto>(Json))!;

        Assert.Equal((0, (DateTimeOffset?)null, (string?)null), (room.TurnNumber, room.TurnEndsAt, room.TurnOf));
    }

    [PostgresFact]
    public async Task A_player_acting_out_of_turn_is_refused_without_a_change()
    {
        var alice = await GuestAsync("Alice");
        var bob = await GuestAsync("Bob");
        using (alice)
        using (bob)
        {
            var room = await StartedAsync("chog-life", alice, bob);

            await ProblemAsync(await SendAsync(bob, room), HttpStatusCode.Conflict, "not_your_turn");
            await ProblemAsync(await SendAsync(bob, room, "END"), HttpStatusCode.Conflict, "not_your_turn");

            Assert.Equal((room.Version, 1, (string?)null), await StoredAsync(room));
        }
    }

    [PostgresFact]
    public async Task Ending_a_turn_passes_it_to_the_next_player_in_join_order()
    {
        var alice = await GuestAsync("Alice");
        var bob = await GuestAsync("Bob");
        var carol = await GuestAsync("Carol");
        using (alice)
        using (bob)
        using (carol)
        {
            var room = await StartedAsync("chog-life", alice, bob, carol);
            (await SendAsync(alice, room)).Dispose();
            room = await ViewAsync(alice, room.Code);
            Assert.Equal((1, "Alice"), (room.TurnNumber, room.TurnOf)); // ADD doesn't end the turn

            _clock.Advance(TimeSpan.FromSeconds(10));
            using (var ended = await SendAsync(alice, room, "END"))
            {
                Assert.Equal(HttpStatusCode.OK, ended.StatusCode);
            }

            room = await ViewAsync(carol, room.Code);
            Assert.Equal((2, "Bob", _clock.Now + Turn), (room.TurnNumber, room.TurnOf, room.TurnEndsAt));

            (await SendAsync(bob, room, "END")).Dispose();
            room = await ViewAsync(carol, room.Code);
            (await SendAsync(carol, room, "END")).Dispose();
            room = await ViewAsync(bob, room.Code);
            Assert.Equal((4, "Alice"), (room.TurnNumber, room.TurnOf));
        }
    }

    [PostgresFact]
    public async Task A_command_after_the_turn_ran_out_is_refused()
    {
        var alice = await GuestAsync("Alice");
        var bob = await GuestAsync("Bob");
        using (alice)
        using (bob)
        {
            var room = await StartedAsync("chog-life", alice, bob);
            _clock.Advance(Turn);

            await ProblemAsync(await SendAsync(alice, room), HttpStatusCode.Conflict, "turn_expired");

            Assert.Equal((room.Version, 1, (string?)null), await StoredAsync(room));
        }
    }

    [PostgresFact]
    public async Task Nothing_times_out_before_the_turn_ends()
    {
        var alice = await GuestAsync("Alice");
        var bob = await GuestAsync("Bob");
        using (alice)
        using (bob)
        {
            var room = await StartedAsync("chog-life", alice, bob);
            _clock.Advance(Turn - TimeSpan.FromSeconds(1));

            Assert.Empty(await SweepAsync());

            Assert.Equal((room.Version, 1, (string?)null), await StoredAsync(room));
        }
    }

    [PostgresFact]
    public async Task An_expired_turn_advances_exactly_once_however_many_sweeps_run()
    {
        var alice = await GuestAsync("Alice");
        var bob = await GuestAsync("Bob");
        using (alice)
        using (bob)
        {
            var room = await StartedAsync("chog-life", alice, bob);
            _clock.Advance(Turn);

            var sweeps = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => SweepAsync()));

            var timedOut = Assert.Single(sweeps.SelectMany(s => s));
            Assert.Equal(new TimedOutTurn(room.Code, null), timedOut);
            // The game's safe default was applied once.
            Assert.Equal((room.Version + 1, 2, """{"count":0,"timeouts":1}"""), await StoredAsync(room));
            var seen = await ViewAsync(alice, room.Code);
            Assert.Equal(("Bob", _clock.Now + Turn, room.Version + 1), (seen.TurnOf, seen.TurnEndsAt, seen.Version));
            Assert.Empty(await SweepAsync());
        }
    }

    [PostgresFact]
    public async Task A_game_without_rules_skips_the_turn()
    {
        var alice = await GuestAsync("Alice");
        var bob = await GuestAsync("Bob");
        using (alice)
        using (bob)
        {
            var room = await StartedAsync("chog-race", alice, bob);
            _clock.Advance(Turn);

            Assert.Single(await SweepAsync());

            Assert.Equal((room.Version + 1, 2, (string?)null), await StoredAsync(room));
            Assert.Equal("Bob", (await ViewAsync(alice, room.Code)).TurnOf);
        }
    }

    [PostgresFact]
    public async Task Missing_three_turns_in_a_row_forfeits_and_an_all_idle_match_is_abandoned()
    {
        var alice = await GuestAsync("Alice");
        var bob = await GuestAsync("Bob");
        var carol = await GuestAsync("Carol");
        using (alice)
        using (bob)
        using (carol)
        {
            var room = await StartedAsync("chog-race", alice, bob, carol);
            for (var sweep = 1; sweep <= 6; sweep++)
            {
                _clock.Advance(Turn);
                Assert.Equal(new TimedOutTurn(room.Code, null), Assert.Single(await SweepAsync()));
            }

            // Alice's third miss in a row.
            _clock.Advance(Turn);
            var forfeit = Assert.Single(await SweepAsync());
            Assert.NotNull(forfeit.ForfeitedPlayerId);
            room = await ViewAsync(bob, room.Code);
            Assert.Equal((nameof(RoomStatus.Active), "Bob"), (room.Status, room.TurnOf));
            Assert.True(room.Members.Single(m => m.DisplayName == "Alice").HasLeft);

            // A player who forfeited is free to play elsewhere.
            using (var other = await alice.PostAsJsonAsync("/api/rooms", new { gameType = "chog-life" }))
            {
                Assert.Equal(HttpStatusCode.Created, other.StatusCode);
            }

            // Bob's third miss leaves Carol alone.
            _clock.Advance(Turn);
            Assert.NotNull(Assert.Single(await SweepAsync()).ForfeitedPlayerId);
            room = await ViewAsync(carol, room.Code);
            Assert.Equal((nameof(RoomStatus.Abandoned), (DateTimeOffset?)null, (string?)null), (room.Status, room.TurnEndsAt, room.TurnOf));
            Assert.Empty(await SweepAsync());
        }
    }

    [PostgresFact]
    public async Task Acting_resets_the_missed_turns()
    {
        var alice = await GuestAsync("Alice");
        var bob = await GuestAsync("Bob");
        using (alice)
        using (bob)
        {
            var room = await StartedAsync("chog-life", alice, bob);
            async Task AliceMissesAsync()
            {
                _clock.Advance(Turn);
                Assert.Single(await SweepAsync());
                room = await ViewAsync(bob, room.Code);
                (await SendAsync(bob, room, "END")).Dispose();
                room = await ViewAsync(alice, room.Code);
            }

            await AliceMissesAsync();
            await AliceMissesAsync();
            (await SendAsync(alice, room, "END")).Dispose();
            room = await ViewAsync(bob, room.Code);
            (await SendAsync(bob, room, "END")).Dispose();
            await AliceMissesAsync();
            await AliceMissesAsync();

            // Four misses, but never three in a row.
            Assert.Equal((nameof(RoomStatus.Active), "Alice"), (room.Status, room.TurnOf));
            Assert.False(room.Members.Single(m => m.DisplayName == "Alice").HasLeft);
        }
    }

    [PostgresFact]
    public async Task Leaving_on_your_turn_passes_it_on()
    {
        var alice = await GuestAsync("Alice");
        var bob = await GuestAsync("Bob");
        var carol = await GuestAsync("Carol");
        using (alice)
        using (bob)
        using (carol)
        {
            var room = await StartedAsync("chog-life", alice, bob, carol);
            _clock.Advance(TimeSpan.FromSeconds(5));

            (await alice.PostAsync($"/api/rooms/{room.Code}/leave", null)).Dispose();

            room = await ViewAsync(bob, room.Code);
            Assert.Equal((2, "Bob", _clock.Now + Turn), (room.TurnNumber, room.TurnOf, room.TurnEndsAt));
        }
    }

    [PostgresFact]
    public async Task The_scheduler_times_out_overdue_turns_by_itself()
    {
        await _factory.DisposeAsync();
        _factory = Factory(withScheduler: true);
        var alice = await GuestAsync("Alice");
        var bob = await GuestAsync("Bob");
        using (alice)
        using (bob)
        {
            var room = await StartedAsync("chog-life", alice, bob);
            _clock.Advance(Turn);

            var deadline = DateTime.UtcNow.AddSeconds(10);
            while ((await ViewAsync(alice, room.Code)).TurnNumber == 1 && DateTime.UtcNow < deadline)
            {
                await Task.Delay(50);
            }

            Assert.Equal((room.Version + 1, 2, """{"count":0,"timeouts":1}"""), await StoredAsync(room));
        }
    }

    [PostgresTheory]
    [InlineData("""UPDATE "GameRooms" SET "CurrentPlayerId" = NULL WHERE "Status" = 'Active'""", "CK_GameRooms_CurrentTurn")]
    [InlineData("""UPDATE "GameRooms" SET "TurnEndsAt" = NULL WHERE "Status" = 'Active'""", "CK_GameRooms_CurrentTurn")]
    [InlineData("""UPDATE "GameRooms" SET "CurrentPlayerId" = "HostPlayerId", "TurnEndsAt" = now() WHERE "Status" = 'Waiting'""", "CK_GameRooms_CurrentTurn")]
    [InlineData("""UPDATE "GameRooms" SET "TurnNumber" = 0 WHERE "Status" = 'Active'""", "CK_GameRooms_TurnNumber")]
    [InlineData("""UPDATE "GameRooms" SET "TurnNumber" = 1 WHERE "Status" = 'Waiting'""", "CK_GameRooms_TurnNumber")]
    [InlineData("""UPDATE "GameRooms" SET "TurnTimeout" = NULL WHERE "Status" = 'Active'""", "CK_GameRooms_TurnTimeout")]
    [InlineData("""UPDATE "GameRooms" SET "TurnTimeout" = interval '0' WHERE "Status" = 'Active'""", "CK_GameRooms_TurnTimeout")]
    [InlineData("""UPDATE "GameRooms" SET "TurnTimeout" = interval '20 seconds' WHERE "Status" = 'Waiting'""", "CK_GameRooms_TurnTimeout")]
    [InlineData("""UPDATE "RoomMembers" SET "Seat" = 7""", "CK_RoomMembers_Seat")]
    [InlineData("""UPDATE "RoomMembers" SET "Seat" = 0""", "CK_RoomMembers_Seat")]
    [InlineData("""UPDATE "RoomMembers" SET "MissedTurns" = -1""", "CK_RoomMembers_MissedTurns")]
    public async Task Database_rejects_invalid_turns(string sql, string constraint)
    {
        var alice = await GuestAsync("Alice");
        var bob = await GuestAsync("Bob");
        var carol = await GuestAsync("Carol");
        using (alice)
        using (bob)
        using (carol)
        {
            await StartedAsync("chog-life", alice, bob);
            (await carol.PostAsJsonAsync("/api/rooms", new { gameType = "chog-life" })).Dispose(); // a waiting room
            await using var db = database.CreateDbContext();

            var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(sql));

            Assert.Equal(("23514", constraint), (error.SqlState, error.ConstraintName));
        }
    }

    [PostgresFact]
    public async Task Database_keeps_one_player_per_seat()
    {
        var alice = await GuestAsync("Alice");
        var bob = await GuestAsync("Bob");
        using (alice)
        using (bob)
        {
            await StartedAsync("chog-life", alice, bob);
            await using var db = database.CreateDbContext();

            var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync("""UPDATE "RoomMembers" SET "Seat" = 1"""));

            Assert.Equal(PostgresErrorCodes.UniqueViolation, error.SqlState);
        }
    }

    [Theory]
    [InlineData("Matches:TurnTimeout", "00:00:04", "Matches:TurnTimeout must be between")]
    [InlineData("Matches:TurnTimeout", "00:10:01", "Matches:TurnTimeout must be between")]
    [InlineData("Matches:MaxMissedTurns", "0", "Matches:MaxMissedTurns must be between 1 and 20")]
    [InlineData("Matches:TimeoutCheckInterval", "00:00:00", "Matches:TimeoutCheckInterval must be between")]
    public async Task Invalid_turn_settings_stop_startup(string key, string value, string expectedMessage)
    {
        await using var factory = new ApiFactory(ApiFactory.UnusedDatabase, new Dictionary<string, string?> { [key] = value });

        var error = Assert.Throws<OptionsValidationException>(() => factory.CreateClient());
        Assert.Contains(expectedMessage, error.Message);
    }

    [Fact]
    public void Turn_settings_default_to_a_twenty_second_turn_and_three_misses()
    {
        var defaults = new MatchOptions();

        Assert.Equal((TimeSpan.FromSeconds(20), 3, TimeSpan.FromSeconds(1)), (defaults.TurnTimeout, defaults.MaxMissedTurns, defaults.TimeoutCheckInterval));
    }

    /// <summary>
    /// A stand-in game: <c>ADD</c> adds one to a counter and keeps the turn, <c>END</c> ends it. Its safe default for a
    /// turn that runs out counts the timeout, so a test can see it was applied exactly once.
    /// </summary>
    private sealed class TurnRules : IMatchRules
    {
        public string GameType => "chog-life";

        public MatchRuleDecision Apply(MatchCommandContext context) => context.Type switch
        {
            "ADD" => MatchRuleDecision.Accept(With(context.State, count: Count(context.State) + 1)),
            "END" => MatchRuleDecision.Accept(With(context.State), endsTurn: true),
            _ => MatchRuleDecision.Unknown,
        };

        public JsonElement? TimeOutTurn(MatchTurnContext context) => With(context.State, timeouts: Timeouts(context.State) + 1);

        public JsonElement? ViewFor(MatchViewContext context) => context.State;

        private static int Count(JsonElement? state) => state?.GetProperty("count").GetInt32() ?? 0;

        private static int Timeouts(JsonElement? state) => state?.GetProperty("timeouts").GetInt32() ?? 0;

        private static JsonElement With(JsonElement? state, int? count = null, int? timeouts = null) =>
            JsonSerializer.SerializeToElement(new { count = count ?? Count(state), timeouts = timeouts ?? Timeouts(state) });
    }
}
