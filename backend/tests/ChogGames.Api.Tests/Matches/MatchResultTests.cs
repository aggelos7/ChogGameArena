using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ChogGames.Api.Matches;
using ChogGames.Core.Matches;
using ChogGames.Core.Rooms;
using ChogGames.Infrastructure.Matches;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace ChogGames.Api.Tests.Matches;

/// <summary>Match results, player stats and the restart policy (STORY-028, ENG-010, ENG-014, DEC-030).</summary>
public sealed class MatchResultTests(MigratedPostgresDatabase database) : IClassFixture<MigratedPostgresDatabase>, IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static readonly TimeSpan Turn = TimeSpan.FromSeconds(30);

    // Whole seconds, so times read back from Postgres (microseconds) compare equal.
    private readonly ManualTimeProvider _clock = new(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));

    private ApiFactory _factory = null!;

    private sealed record MemberDto(string DisplayName, bool IsYou, bool HasLeft, bool IsTurn, int? Rank);

    private sealed record RoomDto(
        string Code, Guid MatchId, long Version, string Status, int TurnNumber, DateTimeOffset? TurnEndsAt, MemberDto[] Members, JsonElement? State)
    {
        public int? RankOf(string name) => Members.Single(m => m.DisplayName == name).Rank;

        public string? TurnOf => Members.SingleOrDefault(m => m.IsTurn)?.DisplayName;
    }

    private sealed record ProfileDto(int GamesPlayed, int Wins);

    private sealed record ProblemDto(int Status, string? Code);

    public async Task InitializeAsync()
    {
        if (database.ConnectionString.Length > 0)
        {
            await using var db = database.CreateDbContext();
            await db.Database.ExecuteSqlRawAsync("TRUNCATE \"GuestSessions\", \"Players\" CASCADE");
        }

        _factory = Factory();
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    /// <summary>A server process; tests time out turns themselves, so the scheduler doesn't run.</summary>
    private ApiFactory Factory() =>
        new(
            database.ConnectionString,
            new Dictionary<string, string?>
            {
                ["Games:Items:0:Status"] = "Available",
                ["Games:Items:1:Status"] = "Available",
                ["Matches:TurnTimeout"] = Turn.ToString(),
            },
            services =>
            {
                services.AddSingleton<TimeProvider>(_clock);
                services.RemoveAll<IMatchRules>().AddSingleton<IMatchRules, FinishingRules>();
                foreach (var worker in services.Where(d => d.ImplementationType == typeof(TurnTimeoutWorker)).ToList())
                {
                    services.Remove(worker);
                }
            });

    /// <summary>Stops the server and starts a new one on the same database, as a deploy or crash would.</summary>
    private async Task RestartAsync()
    {
        await _factory.DisposeAsync();
        _factory = Factory();
        _factory.CreateClient().Dispose(); // starts the host
    }

    /// <summary>A browser of the current server, sending <paramref name="cookie"/> if given.</summary>
    private HttpClient Browser(string? cookie = null)
    {
        var client = _factory.CreateClient(
            new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), HandleCookies = false });
        if (cookie is not null)
        {
            client.DefaultRequestHeaders.Add("Cookie", cookie);
        }

        return client;
    }

    private async Task<HttpClient> GuestAsync(string name)
    {
        var client = Browser();
        using var response = await client.PostAsJsonAsync("/api/guest-sessions", new { displayName = name });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        client.DefaultRequestHeaders.Add("Cookie", response.Headers.GetValues("Set-Cookie").Single().Split(';')[0]);
        return client;
    }

    /// <summary>The same player on the restarted server: a new client carrying only the session cookie.</summary>
    private HttpClient Resumed(HttpClient before) => Browser(before.DefaultRequestHeaders.GetValues("Cookie").Single());

    private static async Task<RoomDto> ViewAsync(HttpClient client, string code) =>
        (await client.GetFromJsonAsync<RoomDto>($"/api/rooms/{code}", Json))!;

    private static async Task<ProfileDto> ProfileAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<ProfileDto>("/api/profile", Json))!;

    /// <summary>A started match of <paramref name="players"/>, the first hosting and the rest joining a second apart, in order.</summary>
    private async Task<RoomDto> StartedAsync(params HttpClient[] players)
    {
        using var created = await players[0].PostAsJsonAsync("/api/rooms", new { gameType = "chog-life" });
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

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, RoomDto room, string type, Guid? actionId = null) =>
        client.PostAsJsonAsync(
            $"/api/rooms/{room.Code}/commands",
            new { actionId = actionId ?? Guid.NewGuid(), matchId = room.MatchId, expectedVersion = room.Version, type });

    private static async Task ProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        using (response)
        {
            Assert.Equal(status, response.StatusCode);
            var problem = (await response.Content.ReadFromJsonAsync<ProblemDto>(Json))!;
            Assert.Equal(((int)status, code), (problem.Status, problem.Code));
        }
    }

    private async Task<(int GamesPlayed, int Wins)[]> StatsAsync(params HttpClient[] players)
    {
        var stats = new List<(int, int)>();
        foreach (var player in players)
        {
            var profile = await ProfileAsync(player);
            stats.Add((profile.GamesPlayed, profile.Wins));
        }

        return [.. stats];
    }

    [PostgresFact]
    public async Task Finishing_a_match_stores_the_standings_and_updates_every_players_stats()
    {
        var alice = await GuestAsync("Alice");
        var bob = await GuestAsync("Bob");
        var carol = await GuestAsync("Carol");
        using (alice)
        using (bob)
        using (carol)
        {
            var room = await StartedAsync(alice, bob, carol);
            Assert.Equal([(0, 0), (0, 0), (0, 0)], await StatsAsync(alice, bob, carol));

            using var finished = await SendAsync(alice, room, "WIN");
            Assert.Equal(HttpStatusCode.OK, finished.StatusCode);

            var seenByBob = await ViewAsync(bob, room.Code);
            Assert.Equal(("Completed", room.Version + 1, (string?)null), (seenByBob.Status, seenByBob.Version, seenByBob.TurnOf));
            Assert.Equal((1, 2, 3), (seenByBob.RankOf("Alice"), seenByBob.RankOf("Bob"), seenByBob.RankOf("Carol")));
            Assert.Equal([(1, 1), (1, 0), (1, 0)], await StatsAsync(alice, bob, carol));

            // The match is over: nobody can act on it any more, and every player is free to play again.
            await ProblemAsync(await SendAsync(bob, seenByBob, "WIN"), HttpStatusCode.Conflict, "room_closed");
            using var current = await alice.GetAsync("/api/rooms/current");
            Assert.Equal(HttpStatusCode.NoContent, current.StatusCode);
        }
    }

    [PostgresFact]
    public async Task A_waiting_or_active_room_shows_no_ranks()
    {
        var alice = await GuestAsync("Alice");
        var bob = await GuestAsync("Bob");
        using (alice)
        using (bob)
        {
            var room = await StartedAsync(alice, bob);

            Assert.All(room.Members, m => Assert.Null(m.Rank));
        }
    }

    [PostgresFact]
    public async Task Tied_winners_each_get_a_win()
    {
        var alice = await GuestAsync("Alice");
        var bob = await GuestAsync("Bob");
        var carol = await GuestAsync("Carol");
        using (alice)
        using (bob)
        using (carol)
        {
            var room = await StartedAsync(alice, bob, carol);

            (await SendAsync(alice, room, "TIE")).Dispose();

            var after = await ViewAsync(carol, room.Code);
            Assert.Equal((1, 1, 1), (after.RankOf("Alice"), after.RankOf("Bob"), after.RankOf("Carol")));
            Assert.Equal([(1, 1), (1, 1), (1, 1)], await StatsAsync(alice, bob, carol));
        }
    }

    [PostgresFact]
    public async Task A_player_who_left_ranks_last_and_the_match_still_counts_as_played_for_them()
    {
        var alice = await GuestAsync("Alice");
        var bob = await GuestAsync("Bob");
        var carol = await GuestAsync("Carol");
        using (alice)
        using (bob)
        using (carol)
        {
            var room = await StartedAsync(alice, bob, carol);
            (await carol.PostAsync($"/api/rooms/{room.Code}/leave", null)).Dispose();

            (await SendAsync(alice, await ViewAsync(alice, room.Code), "WIN")).Dispose();

            var after = await ViewAsync(carol, room.Code);
            Assert.Equal(("Completed", 1, 2, 3), (after.Status, after.RankOf("Alice"), after.RankOf("Bob"), after.RankOf("Carol")));
            Assert.Equal([(1, 1), (1, 0), (1, 0)], await StatsAsync(alice, bob, carol));
        }
    }

    [PostgresFact]
    public async Task An_abandoned_match_has_no_winner_and_changes_no_stats()
    {
        var alice = await GuestAsync("Alice");
        var bob = await GuestAsync("Bob");
        using (alice)
        using (bob)
        {
            var room = await StartedAsync(alice, bob);

            (await bob.PostAsync($"/api/rooms/{room.Code}/leave", null)).Dispose();

            var after = await ViewAsync(alice, room.Code);
            Assert.Equal("Abandoned", after.Status);
            Assert.All(after.Members, m => Assert.Null(m.Rank));
            Assert.Equal([(0, 0), (0, 0)], await StatsAsync(alice, bob));
        }
    }

    [PostgresFact]
    public async Task Retrying_the_finishing_command_replays_it_and_counts_the_match_once()
    {
        var alice = await GuestAsync("Alice");
        var bob = await GuestAsync("Bob");
        using (alice)
        using (bob)
        {
            var room = await StartedAsync(alice, bob);
            var actionId = Guid.NewGuid();
            (await SendAsync(alice, room, "WIN", actionId)).Dispose();

            using var retry = await SendAsync(alice, room, "WIN", actionId);

            Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
            Assert.True((await retry.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("replayed").GetBoolean());
            Assert.Equal([(1, 1), (1, 0)], await StatsAsync(alice, bob));
        }
    }

    [PostgresFact]
    public async Task Concurrent_finishing_commands_store_one_winner_and_count_the_match_once()
    {
        var alice = await GuestAsync("Alice");
        var bob = await GuestAsync("Bob");
        using (alice)
        using (bob)
        {
            var room = await StartedAsync(alice, bob);

            var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => SendAsync(alice, room, i % 2 == 0 ? "WIN" : "LOSE")));

            Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK);
            foreach (var response in responses.Where(r => r.StatusCode != HttpStatusCode.OK))
            {
                // Each loser either lost the lock race on the version or found the match already over.
                Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
                var code = (await response.Content.ReadFromJsonAsync<ProblemDto>(Json))!.Code;
                Assert.Contains(code, new[] { "stale_version", "room_closed" });
            }

            var after = await ViewAsync(bob, room.Code);
            Assert.Equal("Completed", after.Status);
            Assert.Single(after.Members, m => m.Rank == 1);
            var stats = await StatsAsync(alice, bob);
            Assert.Equal((1, 1), (stats[0].GamesPlayed, stats[1].GamesPlayed));
            Assert.Equal(1, stats[0].Wins + stats[1].Wins);
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
    }

    [PostgresFact]
    public async Task Standings_that_miss_a_player_fail_without_changing_the_match_or_any_stats()
    {
        var alice = await GuestAsync("Alice");
        var bob = await GuestAsync("Bob");
        using (alice)
        using (bob)
        {
            var room = await StartedAsync(alice, bob);

            // A rules bug, so the request fails (TestServer rethrows what would be a 500) and its transaction rolls back.
            await Assert.ThrowsAsync<InvalidOperationException>(() => SendAsync(alice, room, "BAD_FINISH"));

            var after = await ViewAsync(alice, room.Code);
            Assert.Equal(("Active", room.Version, "Alice"), (after.Status, after.Version, after.TurnOf));
            Assert.All(after.Members, m => Assert.Null(m.Rank));
            Assert.Equal([(0, 0), (0, 0)], await StatsAsync(alice, bob));
        }
    }

    [PostgresFact]
    public async Task A_restart_resumes_the_match_with_a_full_turn_for_the_current_player()
    {
        var alice = await GuestAsync("Alice");
        var bob = await GuestAsync("Bob");
        using (alice)
        using (bob)
        {
            var room = await StartedAsync(alice, bob);
            (await SendAsync(alice, room, "ADD")).Dispose();
            (await SendAsync(alice, await ViewAsync(alice, room.Code), "END")).Dispose();
            var before = await ViewAsync(bob, room.Code);
            Assert.Equal("Bob", before.TurnOf);

            // The server is down for longer than Bob's turn.
            _clock.Advance(Turn * 3);
            await RestartAsync();
            using var aliceAgain = Resumed(alice);
            using var bobAgain = Resumed(bob);

            var after = await ViewAsync(bobAgain, room.Code);
            Assert.Equal(("Active", before.Version + 1, before.TurnNumber, "Bob"), (after.Status, after.Version, after.TurnNumber, after.TurnOf));
            Assert.Equal(_clock.Now + Turn, after.TurnEndsAt);
            Assert.Equal(before.State!.Value.GetRawText(), after.State!.Value.GetRawText());

            // The downtime didn't cost Bob a turn: nothing is overdue, and nothing was counted as missed.
            await using (var scope = _factory.Services.CreateAsyncScope())
            {
                Assert.Empty(await scope.ServiceProvider.GetRequiredService<TurnTimeoutService>().TimeOutOverdueTurnsAsync(CancellationToken.None));
            }

            await using var db = database.CreateDbContext();
            var stored = await db.GameRooms.AsNoTracking().SingleAsync(r => r.Id == room.MatchId);
            Assert.All(stored.Members, m => Assert.Equal(0, m.MissedTurns));

            // Commands built before the restart are stale; Bob plays on from the resumed snapshot.
            await ProblemAsync(await SendAsync(bobAgain, before, "ADD"), HttpStatusCode.Conflict, "stale_version");
            using var played = await SendAsync(bobAgain, after, "ADD");
            Assert.Equal(HttpStatusCode.OK, played.StatusCode);
        }
    }

    [PostgresFact]
    public async Task A_restart_leaves_waiting_and_finished_rooms_as_they_were()
    {
        var alice = await GuestAsync("Alice");
        var bob = await GuestAsync("Bob");
        var carol = await GuestAsync("Carol");
        using (alice)
        using (bob)
        using (carol)
        {
            var finished = await StartedAsync(alice, bob);
            (await SendAsync(alice, finished, "WIN")).Dispose();
            using var created = await carol.PostAsJsonAsync("/api/rooms", new { gameType = "chog-life" });
            var waiting = (await created.Content.ReadFromJsonAsync<RoomDto>(Json))!;
            var before = (await ViewAsync(alice, finished.Code), await ViewAsync(carol, waiting.Code));

            _clock.Advance(TimeSpan.FromMinutes(5));
            await RestartAsync();
            using var aliceAgain = Resumed(alice);
            using var carolAgain = Resumed(carol);

            var after = (await ViewAsync(aliceAgain, finished.Code), await ViewAsync(carolAgain, waiting.Code));
            Assert.Equal((before.Item1.Version, before.Item1.Status), (after.Item1.Version, after.Item1.Status));
            Assert.Equal((before.Item2.Version, before.Item2.Status), (after.Item2.Version, after.Item2.Status));
            Assert.Equal([(1, 1), (1, 0)], await StatsAsync(aliceAgain, Resumed(bob)));
        }
    }

    [PostgresFact]
    public async Task Every_restart_resumes_the_match_again_and_never_finishes_it()
    {
        var alice = await GuestAsync("Alice");
        var bob = await GuestAsync("Bob");
        using (alice)
        using (bob)
        {
            var room = await StartedAsync(alice, bob);

            for (var i = 1; i <= 3; i++)
            {
                _clock.Advance(Turn);
                await RestartAsync();
                using var aliceAgain = Resumed(alice);
                var after = await ViewAsync(aliceAgain, room.Code);
                Assert.Equal(("Active", room.Version + i, "Alice", _clock.Now + Turn), (after.Status, after.Version, after.TurnOf, after.TurnEndsAt));
            }
        }
    }

    [PostgresTheory]
    [InlineData("""UPDATE "RoomMembers" SET "Rank" = 0""", "CK_RoomMembers_Rank")]
    [InlineData("""UPDATE "RoomMembers" SET "Rank" = 7""", "CK_RoomMembers_Rank")]
    [InlineData("""UPDATE "RoomMembers" SET "Rank" = 1 WHERE "Seat" IS NULL""", "CK_RoomMembers_Rank")]
    [InlineData("""UPDATE "RoomMembers" SET "Result" = '{{}}' WHERE "Seat" IS NOT NULL""", "CK_RoomMembers_Result")]
    [InlineData("""UPDATE "RoomMembers" SET "Rank" = 1, "Result" = '[]' WHERE "Seat" IS NOT NULL""", "CK_RoomMembers_Result")]
    [InlineData("""UPDATE "RoomMembers" SET "Rank" = 1, "Result" = '{{}}', "LeftAt" = now() WHERE "Seat" IS NOT NULL""", "CK_RoomMembers_Result")]
    public async Task Database_rejects_invalid_ranks_and_results(string sql, string constraint)
    {
        var alice = await GuestAsync("Alice");
        var bob = await GuestAsync("Bob");
        var carol = await GuestAsync("Carol");
        using (alice)
        using (bob)
        using (carol)
        {
            await StartedAsync(alice, bob);
            (await carol.PostAsJsonAsync("/api/rooms", new { gameType = "chog-life" })).Dispose(); // a waiting room, no seats
            await using var db = database.CreateDbContext();

            var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(sql));

            Assert.Equal(("23514", constraint), (error.SqlState, error.ConstraintName));
        }
    }

    /// <summary>
    /// A stand-in game: <c>ADD</c> counts and keeps the turn, <c>END</c> ends it. <c>WIN</c> finishes the match with the
    /// player first and the others in seat order, <c>LOSE</c> with the player last, <c>TIE</c> with everyone first, and
    /// <c>BAD_FINISH</c> with only the player ranked, which the engine must refuse.
    /// </summary>
    private sealed class FinishingRules : IMatchRules
    {
        public string GameType => "chog-life";

        public MatchRuleDecision Apply(MatchCommandContext context)
        {
            var playing = context.Match.Members.Where(m => !m.HasLeft).OrderBy(m => m.Seat).Select(m => m.PlayerId).ToList();
            var others = playing.Where(id => id != context.PlayerId).ToList();
            var state = JsonSerializer.SerializeToElement(new { count = Count(context.State) + (context.Type == "ADD" ? 1 : 0) });
            return context.Type switch
            {
                "ADD" => MatchRuleDecision.Accept(state),
                "END" => MatchRuleDecision.Accept(state, endsTurn: true),
                "WIN" => MatchRuleDecision.Finish(state, [new(context.PlayerId, 1), .. others.Select((id, i) => new MatchStanding(id, i + 2))]),
                "LOSE" => MatchRuleDecision.Finish(state, [.. others.Select((id, i) => new MatchStanding(id, i + 1)), new(context.PlayerId, playing.Count)]),
                "TIE" => MatchRuleDecision.Finish(state, [.. playing.Select(id => new MatchStanding(id, 1))]),
                "BAD_FINISH" => MatchRuleDecision.Finish(state, [new(context.PlayerId, 1)]),
                _ => MatchRuleDecision.Unknown,
            };
        }

        public JsonElement? TimeOutTurn(MatchTurnContext context) => null;

        public JsonElement? ViewFor(MatchViewContext context) => context.State;

        private static int Count(JsonElement? state) => state?.GetProperty("count").GetInt32() ?? 0;
    }
}
