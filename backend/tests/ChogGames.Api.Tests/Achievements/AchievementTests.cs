using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using ChogGames.Api.Matches;
using ChogGames.Core.Achievements;
using ChogGames.Core.Matches;
using ChogGames.Infrastructure.Persistence.Migrations;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace ChogGames.Api.Tests.Achievements;

/// <summary>Cross-game achievements from verified match results (STORY-058, DEC-063).</summary>
public sealed class AchievementTests(MigratedPostgresDatabase database) : IClassFixture<MigratedPostgresDatabase>, IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // Whole seconds, so times read back from Postgres (microseconds) compare equal.
    private readonly ManualTimeProvider _clock = new(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));

    private ApiFactory _factory = null!;

    private sealed record AchievementDto(string Id, string Name, string Description, int Progress, int Target, DateTimeOffset? UnlockedAt);

    private sealed record UnlockedDto(string Id, string Name, string Description);

    private sealed record RoomDto(string Code, Guid MatchId, long Version, string Status, UnlockedDto[] Achievements)
    {
        public string[] Unlocked => [.. Achievements.Select(a => a.Id)];
    }

    private sealed record ProblemDto(int Status, string? Code);

    private sealed record AwardRow(Guid PlayerId, string AchievementId, Guid RoomId, DateTimeOffset UnlockedAt);

    public async Task InitializeAsync()
    {
        if (database.ConnectionString.Length > 0)
        {
            await using var db = database.CreateDbContext();
            await db.Database.ExecuteSqlRawAsync("TRUNCATE \"GuestSessions\", \"Players\" CASCADE");
        }

        _factory = new ApiFactory(
            database.ConnectionString,
            new Dictionary<string, string?>
            {
                ["Games:Items:0:Status"] = "Available",
                ["Games:Items:1:Status"] = "Available",
                ["Rooms:RequestsPerMinutePerPlayer"] = "1000", // whole matches, many in a row
            },
            services =>
            {
                services.AddSingleton<TimeProvider>(_clock);
                services.RemoveAll<IMatchRules>()
                    .AddSingleton<IMatchRules>(new FinishingRules("chog-life"))
                    .AddSingleton<IMatchRules>(new FinishingRules("chog-race"));
                foreach (var worker in services.Where(d => d.ImplementationType == typeof(TurnTimeoutWorker)).ToList())
                {
                    services.Remove(worker);
                }
            });
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    private HttpClient Browser() =>
        _factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), HandleCookies = false });

    private async Task<HttpClient> GuestAsync(string name)
    {
        var client = Browser();
        using var response = await client.PostAsJsonAsync("/api/guest-sessions", new { displayName = name });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        client.DefaultRequestHeaders.Add("Cookie", response.Headers.GetValues("Set-Cookie").Single().Split(';')[0]);
        return client;
    }

    private static async Task<RoomDto> ViewAsync(HttpClient client, string code) =>
        (await client.GetFromJsonAsync<RoomDto>($"/api/rooms/{code}", Json))!;

    private static async Task<AchievementDto[]> AchievementsAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<AchievementDto[]>("/api/profile/achievements", Json))!;

    private static async Task<AchievementDto> AchievementAsync(HttpClient client, string id) =>
        (await AchievementsAsync(client)).Single(a => a.Id == id);

    private static async Task<string[]> UnlockedAsync(HttpClient client) =>
        [.. (await AchievementsAsync(client)).Where(a => a.UnlockedAt is not null).Select(a => a.Id)];

    /// <summary>A started match of <paramref name="gameType"/>, the first player hosting; each match ends a minute later.</summary>
    private async Task<RoomDto> StartedAsync(string gameType, params HttpClient[] players)
    {
        _clock.Advance(TimeSpan.FromMinutes(1));
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

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, RoomDto room, string type, Guid? actionId = null) =>
        client.PostAsJsonAsync(
            $"/api/rooms/{room.Code}/commands",
            new { actionId = actionId ?? Guid.NewGuid(), matchId = room.MatchId, expectedVersion = room.Version, type });

    /// <summary>A whole match of <paramref name="gameType"/> that <paramref name="winner"/> (the host) ends with <paramref name="command"/>.</summary>
    private async Task<RoomDto> PlayAsync(string gameType, HttpClient winner, HttpClient other, string command = "WIN")
    {
        var room = await StartedAsync(gameType, winner, other);
        using var finished = await SendAsync(winner, room, command);
        Assert.Equal(HttpStatusCode.OK, finished.StatusCode);
        return await ViewAsync(winner, room.Code);
    }

    private async Task<AwardRow[]> AwardsAsync()
    {
        await using var db = database.CreateDbContext();
        return [.. (await db.PlayerAchievements.AsNoTracking().ToListAsync())
            .Select(a => new AwardRow(a.PlayerId, a.AchievementId, a.RoomId, a.UnlockedAt))
            .OrderBy(a => a.PlayerId).ThenBy(a => a.AchievementId, StringComparer.Ordinal)];
    }

    [PostgresFact]
    public async Task A_new_player_sees_the_whole_catalog_locked_with_no_progress()
    {
        using var alice = await GuestAsync("Alice");

        var achievements = await AchievementsAsync(alice);

        Assert.Equal(
            ["first_match", "regular", "veteran", "first_win", "champion", "explorer", "full_degen"],
            achievements.Select(a => a.Id));
        Assert.All(achievements, a => Assert.Equal((0, (DateTimeOffset?)null), (a.Progress, a.UnlockedAt)));
        Assert.Equal(("Regular", "Complete 10 matches.", 10), (achievements[1].Name, achievements[1].Description, achievements[1].Target));
    }

    [PostgresFact]
    public async Task A_completed_match_unlocks_First_Match_for_everyone_and_First_Win_for_the_winner_shown_only_to_each_of_them()
    {
        var alice = await GuestAsync("Alice");
        var bob = await GuestAsync("Bob");
        using (alice)
        using (bob)
        {
            var room = await StartedAsync("chog-life", alice, bob);
            Assert.Empty((await ViewAsync(alice, room.Code)).Achievements);

            (await SendAsync(alice, room, "WIN")).Dispose();

            Assert.Equal(["first_match", "first_win"], (await ViewAsync(alice, room.Code)).Unlocked);
            Assert.Equal(["first_match"], (await ViewAsync(bob, room.Code)).Unlocked);
            Assert.Equal(["first_match", "first_win"], await UnlockedAsync(alice));
            Assert.Equal(["first_match"], await UnlockedAsync(bob));

            var firstWin = await AchievementAsync(alice, "first_win");
            Assert.Equal((1, 1, (DateTimeOffset?)_clock.GetUtcNow()), (firstWin.Progress, firstWin.Target, firstWin.UnlockedAt));
            Assert.Equal((1, 1), ((await AchievementAsync(alice, "regular")).Progress, (await AchievementAsync(alice, "champion")).Progress));
            Assert.Equal((0, (DateTimeOffset?)null), ((await AchievementAsync(bob, "first_win")).Progress, (await AchievementAsync(bob, "first_win")).UnlockedAt));
            Assert.Equal(1, (await AchievementAsync(bob, "explorer")).Progress);
        }
    }

    [PostgresFact]
    public async Task An_achievement_is_unlocked_once_and_keeps_the_match_that_earned_it()
    {
        var alice = await GuestAsync("Alice");
        var bob = await GuestAsync("Bob");
        using (alice)
        using (bob)
        {
            var first = await PlayAsync("chog-life", alice, bob);
            var unlockedAt = (await AchievementAsync(alice, "first_win")).UnlockedAt;

            var second = await PlayAsync("chog-life", alice, bob);

            Assert.Equal(unlockedAt, (await AchievementAsync(alice, "first_win")).UnlockedAt);
            Assert.Equal(2, (await AchievementAsync(alice, "champion")).Progress);
            Assert.Equal(["first_match", "first_win"], (await ViewAsync(alice, first.Code)).Unlocked);
            Assert.Empty((await ViewAsync(alice, second.Code)).Achievements);
            Assert.Equal(3, (await AwardsAsync()).Length);
        }
    }

    [PostgresFact]
    public async Task Ten_matches_unlock_Regular_and_ten_wins_Champion()
    {
        var alice = await GuestAsync("Alice");
        var bob = await GuestAsync("Bob");
        using (alice)
        using (bob)
        {
            RoomDto last = null!;
            for (var match = 0; match < 10; match++)
            {
                last = await PlayAsync("chog-life", alice, bob);
            }

            Assert.Equal(["regular", "champion"], (await ViewAsync(alice, last.Code)).Unlocked);
            Assert.Equal(["regular"], (await ViewAsync(bob, last.Code)).Unlocked);
            Assert.Equal(10, (await AchievementAsync(alice, "veteran")).Progress);
            Assert.Equal(["first_match", "regular", "first_win", "champion"], await UnlockedAsync(alice));
        }
    }

    [PostgresFact]
    public async Task Explorer_needs_a_completed_match_in_two_different_games()
    {
        var alice = await GuestAsync("Alice");
        var bob = await GuestAsync("Bob");
        using (alice)
        using (bob)
        {
            await PlayAsync("chog-life", alice, bob);
            await PlayAsync("chog-life", alice, bob);
            Assert.Equal((1, (DateTimeOffset?)null), ((await AchievementAsync(alice, "explorer")).Progress, (await AchievementAsync(alice, "explorer")).UnlockedAt));

            var race = await PlayAsync("chog-race", alice, bob);

            Assert.Equal(["explorer"], (await ViewAsync(alice, race.Code)).Unlocked);
            Assert.Equal(["explorer"], (await ViewAsync(bob, race.Code)).Unlocked);
            Assert.Equal(2, (await AchievementAsync(bob, "explorer")).Progress);
        }
    }

    [PostgresFact]
    public async Task Full_Degen_needs_a_degen_score_of_100_in_any_game()
    {
        var alice = await GuestAsync("Alice");
        var bob = await GuestAsync("Bob");
        using (alice)
        using (bob)
        {
            await PlayAsync("chog-life", alice, bob, "DEGEN_90");
            Assert.Equal((90, (DateTimeOffset?)null), ((await AchievementAsync(alice, "full_degen")).Progress, (await AchievementAsync(alice, "full_degen")).UnlockedAt));
            Assert.Equal(0, (await AchievementAsync(bob, "full_degen")).Progress);

            var race = await PlayAsync("chog-race", alice, bob, "DEGEN_100");

            Assert.Equal(["explorer", "full_degen"], (await ViewAsync(alice, race.Code)).Unlocked);
            Assert.Equal(100, (await AchievementAsync(alice, "full_degen")).Progress);
            Assert.DoesNotContain("full_degen", await UnlockedAsync(bob));
        }
    }

    [PostgresFact]
    public async Task Retrying_the_finishing_command_awards_nothing_twice()
    {
        var alice = await GuestAsync("Alice");
        var bob = await GuestAsync("Bob");
        using (alice)
        using (bob)
        {
            var room = await StartedAsync("chog-life", alice, bob);
            var actionId = Guid.NewGuid();
            (await SendAsync(alice, room, "WIN", actionId)).Dispose();
            var awarded = await AwardsAsync();
            _clock.Advance(TimeSpan.FromMinutes(5));

            using var retry = await SendAsync(alice, room, "WIN", actionId);

            Assert.True((await retry.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("replayed").GetBoolean());
            Assert.Equal(awarded, await AwardsAsync());
            Assert.Equal(3, awarded.Length);
        }
    }

    [PostgresFact]
    public async Task Concurrent_finishing_commands_award_each_achievement_once()
    {
        var alice = await GuestAsync("Alice");
        var bob = await GuestAsync("Bob");
        using (alice)
        using (bob)
        {
            var room = await StartedAsync("chog-life", alice, bob);

            var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => SendAsync(alice, room, i % 2 == 0 ? "WIN" : "LOSE")));

            Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK);
            var awarded = await AwardsAsync();
            Assert.Equal(3, awarded.Length);
            Assert.Single(awarded, a => a.AchievementId == "first_win");
            Assert.All(awarded, a => Assert.Equal(room.MatchId, a.RoomId));
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
    }

    [PostgresFact]
    public async Task A_player_who_left_a_completed_match_played_it_but_an_abandoned_match_awards_nothing()
    {
        var alice = await GuestAsync("Alice");
        var bob = await GuestAsync("Bob");
        var carol = await GuestAsync("Carol");
        using (alice)
        using (bob)
        using (carol)
        {
            var abandoned = await StartedAsync("chog-life", alice, bob);
            (await bob.PostAsync($"/api/rooms/{abandoned.Code}/leave", null)).Dispose();
            Assert.Equal("Abandoned", (await ViewAsync(alice, abandoned.Code)).Status);
            Assert.Empty(await AwardsAsync());
            Assert.Empty((await ViewAsync(alice, abandoned.Code)).Achievements);

            var room = await StartedAsync("chog-life", alice, bob, carol);
            (await carol.PostAsync($"/api/rooms/{room.Code}/leave", null)).Dispose();
            (await SendAsync(alice, await ViewAsync(alice, room.Code), "DEGEN_100")).Dispose();

            Assert.Equal(["first_match"], await UnlockedAsync(carol));
            Assert.Equal(["first_match"], (await ViewAsync(carol, room.Code)).Unlocked);
            Assert.Equal(["first_match", "first_win", "full_degen"], await UnlockedAsync(alice));
        }
    }

    [PostgresFact]
    public async Task No_client_can_set_an_achievement_and_strangers_see_none()
    {
        var alice = await GuestAsync("Alice");
        var bob = await GuestAsync("Bob");
        var mallory = await GuestAsync("Mallory");
        using (alice)
        using (bob)
        using (mallory)
        using (var anonymous = Browser())
        {
            var room = await PlayAsync("chog-life", alice, bob);

            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/profile/achievements")).StatusCode);
            using var post = await mallory.PostAsJsonAsync("/api/profile/achievements", new { id = "champion" });
            Assert.Equal(HttpStatusCode.MethodNotAllowed, post.StatusCode);
            using var patch = await mallory.PatchAsJsonAsync("/api/profile", new { displayName = "Mallory", achievements = new[] { "champion" } });
            Assert.Equal(HttpStatusCode.OK, patch.StatusCode);
            Assert.Empty(await UnlockedAsync(mallory));

            using var peek = await mallory.GetAsync($"/api/rooms/{room.Code}");
            Assert.Equal(HttpStatusCode.NotFound, peek.StatusCode);
            Assert.Equal(3, (await AwardsAsync()).Length);
        }
    }

    [PostgresFact]
    public async Task The_migration_backfill_awards_exactly_what_the_matches_awarded_live()
    {
        var alice = await GuestAsync("Alice");
        var bob = await GuestAsync("Bob");
        var carol = await GuestAsync("Carol");
        using (alice)
        using (bob)
        using (carol)
        {
            for (var match = 0; match < 10; match++)
            {
                await PlayAsync("chog-life", match % 3 == 0 ? bob : alice, match % 3 == 0 ? alice : bob);
            }

            await PlayAsync("chog-race", carol, alice, "DEGEN_100");
            var room = await StartedAsync("chog-race", bob, carol, alice);
            (await alice.PostAsync($"/api/rooms/{room.Code}/leave", null)).Dispose();
            (await SendAsync(bob, await ViewAsync(bob, room.Code), "LOSE")).Dispose();
            var live = await AwardsAsync();
            Assert.Equal(11, live.Length);

            await using (var db = database.CreateDbContext())
            {
                await db.Database.ExecuteSqlRawAsync("DELETE FROM \"PlayerAchievements\"");
                await db.Database.ExecuteSqlRawAsync(AddPlayerAchievements.BackfillSql);
                await db.Database.ExecuteSqlRawAsync(AddPlayerAchievements.BackfillSql); // a second run adds nothing
            }

            Assert.Equal(live, await AwardsAsync());
        }
    }

    /// <summary>A later catalog version backfills its new achievements in its own migration; this one stays version 1.</summary>
    [Fact]
    public void The_backfill_unlocks_the_same_catalog_as_the_server()
    {
        var goals = new Dictionary<AchievementGoal, string>
        {
            [AchievementGoal.GamesPlayed] = "played",
            [AchievementGoal.Wins] = "wins",
            [AchievementGoal.GamesTried] = "tried",
            [AchievementGoal.DegenScore] = "degen",
        };

        var frozen = Regex.Matches(AddPlayerAchievements.BackfillSql, @"\('([a-z_]+)', '([a-z]+)', (\d+)\)")
            .Select(m => (m.Groups[1].Value, m.Groups[2].Value, int.Parse(m.Groups[3].Value)));

        Assert.Equal(Achievement.All.Select(a => (a.Id, goals[a.Goal], a.Target)), frozen);
    }

    [PostgresFact]
    public async Task The_table_refuses_duplicate_malformed_and_orphan_awards()
    {
        var alice = await GuestAsync("Alice");
        var bob = await GuestAsync("Bob");
        using (alice)
        using (bob)
        {
            await PlayAsync("chog-life", alice, bob);
            var award = (await AwardsAsync()).First();
            await using var db = database.CreateDbContext();

            Task InsertAsync(Guid playerId, string id, Guid roomId) => db.Database.ExecuteSqlInterpolatedAsync(
                $"""INSERT INTO "PlayerAchievements" ("PlayerId", "AchievementId", "RoomId", "UnlockedAt") VALUES ({playerId}, {id}, {roomId}, now())""");

            var duplicate = await Assert.ThrowsAsync<PostgresException>(() => InsertAsync(award.PlayerId, award.AchievementId, award.RoomId));
            Assert.Equal(PostgresErrorCodes.UniqueViolation, duplicate.SqlState);
            foreach (var malformed in new[] { "", "First_Match", "1st", "a-b", new string('a', 33) })
            {
                var refused = await Assert.ThrowsAsync<PostgresException>(() => InsertAsync(award.PlayerId, malformed, award.RoomId));
                Assert.Contains(refused.SqlState, new[] { PostgresErrorCodes.CheckViolation, PostgresErrorCodes.StringDataRightTruncation });
            }

            var orphan = await Assert.ThrowsAsync<PostgresException>(() => InsertAsync(award.PlayerId, "veteran", Guid.NewGuid()));
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, orphan.SqlState);
            var nullRoom = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
                $"""INSERT INTO "PlayerAchievements" ("PlayerId", "AchievementId", "RoomId", "UnlockedAt") VALUES ({award.PlayerId}, 'veteran', NULL, now())"""));
            Assert.Equal(PostgresErrorCodes.NotNullViolation, nullRoom.SqlState);
        }
    }

    /// <summary>
    /// Ends the match at once: <c>WIN</c> ranks the sender first, <c>LOSE</c> last, and <c>DEGEN_n</c> is a win with a
    /// result of Degen Score n for the sender and 0 for the others.
    /// </summary>
    private sealed class FinishingRules(string gameType) : IMatchRules
    {
        public string GameType => gameType;

        public MatchRuleDecision Apply(MatchCommandContext context)
        {
            var playing = context.Match.Members.Where(m => !m.HasLeft).OrderBy(m => m.Seat).Select(m => m.PlayerId).ToList();
            var others = playing.Where(id => id != context.PlayerId).ToList();
            var state = JsonSerializer.SerializeToElement(new { over = true });
            if (context.Type.StartsWith("DEGEN_", StringComparison.Ordinal))
            {
                var score = int.Parse(context.Type["DEGEN_".Length..]);
                return MatchRuleDecision.Finish(
                    state,
                    [
                        new(context.PlayerId, 1, Result(score)),
                        .. others.Select((id, i) => new MatchStanding(id, i + 2, Result(0))),
                    ]);
            }

            return context.Type switch
            {
                "WIN" => MatchRuleDecision.Finish(state, [new(context.PlayerId, 1), .. others.Select((id, i) => new MatchStanding(id, i + 2))]),
                "LOSE" => MatchRuleDecision.Finish(state, [.. others.Select((id, i) => new MatchStanding(id, i + 1)), new(context.PlayerId, playing.Count)]),
                _ => MatchRuleDecision.Unknown,
            };
        }

        public JsonElement? TimeOutTurn(MatchTurnContext context) => null;

        public JsonElement? ViewFor(MatchViewContext context) => context.State;

        private static JsonElement Result(int degenScore) => JsonSerializer.SerializeToElement(new { degenScore, degenScoreVersion = 1 });
    }
}
