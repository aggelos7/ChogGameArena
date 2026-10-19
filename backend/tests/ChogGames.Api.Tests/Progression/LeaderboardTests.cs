using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ChogGames.Api.Matches;
using ChogGames.Core.Matches;
using ChogGames.Infrastructure.Persistence.Migrations;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using SkiaSharp;

namespace ChogGames.Api.Tests.Progression;

/// <summary>XP and leaderboards from verified match results (STORY-059, DEC-064).</summary>
public sealed class LeaderboardTests(MigratedPostgresDatabase database) : IClassFixture<MigratedPostgresDatabase>, IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // A whole hour before a new season (UTC month), so tests can play on both sides of it.
    private readonly ManualTimeProvider _clock = new(new DateTimeOffset(2026, 9, 30, 23, 0, 0, TimeSpan.Zero));

    private readonly List<ApiFactory> _factories = [];

    private ApiFactory _factory = null!;

    private sealed record ProfileDto(int GamesPlayed, int Wins, int Xp);

    private sealed record AvatarDto(string Id, string Name, string Color, string? ImageUrl = null);

    private sealed record BadgeDto(string Id, string Name);

    private sealed record EntryDto(int Place, string DisplayName, AvatarDto Avatar, int Xp, int Wins, bool IsYou, BadgeDto? Collector = null);

    private sealed record SeasonDto(DateTimeOffset StartsAt, DateTimeOffset EndsAt);

    private sealed record BoardDto(string Game, string Period, SeasonDto? Season, EntryDto[] Entries, EntryDto? You)
    {
        public (string Name, int Xp, int Wins)[] Places => [.. Entries.Select(e => (e.DisplayName, e.Xp, e.Wins))];
    }

    private sealed record RoomDto(string Code, Guid MatchId, long Version, string Status);

    private sealed record ProblemDto(int Status, string? Code);

    private sealed record AwardRow(Guid RoomId, Guid PlayerId, string GameType, DateTimeOffset EarnedAt, int OpponentsBeaten, bool Won, int Xp, int RulesVersion);

    public async Task InitializeAsync()
    {
        if (database.ConnectionString.Length > 0)
        {
            await using var db = database.CreateDbContext();
            await db.Database.ExecuteSqlRawAsync("TRUNCATE \"GuestSessions\", \"Players\" CASCADE");
        }

        _factory = Factory();
    }

    public async Task DisposeAsync()
    {
        foreach (var factory in _factories)
        {
            await factory.DisposeAsync();
        }
    }

    /// <summary>The API with finishing rules for both games and no turn scheduler, plus <paramref name="settings"/>.</summary>
    private ApiFactory Factory(IReadOnlyDictionary<string, string?>? settings = null)
    {
        var configuration = new Dictionary<string, string?>
        {
            ["Games:Items:0:Status"] = "Available",
            ["Games:Items:1:Status"] = "Available",
            ["Rooms:RequestsPerMinutePerPlayer"] = "1000", // whole matches, many in a row
            ["GuestSessions:CreatePerMinutePerClient"] = "1000",
        };
        foreach (var (key, value) in settings ?? new Dictionary<string, string?>())
        {
            configuration[key] = value;
        }

        var factory = new ApiFactory(
            database.ConnectionString,
            configuration,
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
        _factories.Add(factory);
        return factory;
    }

    private HttpClient Browser(ApiFactory? factory = null) =>
        (factory ?? _factory).CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), HandleCookies = false });

    private async Task<HttpClient> GuestAsync(string name)
    {
        var client = Browser();
        using var response = await client.PostAsJsonAsync("/api/guest-sessions", new { displayName = name });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        client.DefaultRequestHeaders.Add("Cookie", response.Headers.GetValues("Set-Cookie").Single().Split(';')[0]);
        return client;
    }

    /// <summary>The same session against another API instance, such as one with other XP settings.</summary>
    private HttpClient On(ApiFactory factory, HttpClient client)
    {
        var other = Browser(factory);
        other.DefaultRequestHeaders.Add("Cookie", client.DefaultRequestHeaders.GetValues("Cookie").Single());
        return other;
    }

    private static async Task<RoomDto> ViewAsync(HttpClient client, string code) =>
        (await client.GetFromJsonAsync<RoomDto>($"/api/rooms/{code}", Json))!;

    private static async Task<ProfileDto> ProfileAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<ProfileDto>("/api/profile", Json))!;

    private static async Task<BoardDto> BoardAsync(HttpClient client, string query = "") =>
        (await client.GetFromJsonAsync<BoardDto>($"/api/leaderboards{query}", Json))!;

    /// <summary>A started match of <paramref name="gameType"/>, the first player hosting; each match ends a minute later.</summary>
    private async Task<RoomDto> StartedAsync(string gameType, params HttpClient[] players)
    {
        _clock.Advance(TimeSpan.FromMinutes(1));
        using var created = await players[0].PostAsJsonAsync("/api/rooms", new { gameType });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var room = (await created.Content.ReadFromJsonAsync<RoomDto>(Json))!;
        foreach (var player in players[1..])
        {
            _clock.Advance(TimeSpan.FromSeconds(1)); // seats follow join order
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

    /// <summary>
    /// A whole match of <paramref name="gameType"/> that the host, <paramref name="players"/>[0], wins; the others rank
    /// in the order given.
    /// </summary>
    private async Task<RoomDto> PlayAsync(string gameType, params HttpClient[] players)
    {
        var room = await StartedAsync(gameType, players);
        using var finished = await SendAsync(players[0], room, "WIN");
        Assert.Equal(HttpStatusCode.OK, finished.StatusCode);
        return await ViewAsync(players[0], room.Code);
    }

    private async Task<AwardRow[]> AwardsAsync()
    {
        await using var db = database.CreateDbContext();
        return [.. (await db.XpAwards.AsNoTracking().ToListAsync())
            .Select(a => new AwardRow(a.RoomId, a.PlayerId, a.GameType, a.EarnedAt, a.OpponentsBeaten, a.Won, a.Xp, a.RulesVersion))
            .OrderBy(a => a.RoomId).ThenBy(a => a.PlayerId)];
    }

    [PostgresFact]
    public async Task A_completed_match_earns_10_XP_plus_5_per_opponent_beaten()
    {
        var alice = await GuestAsync("Alice");
        var bob = await GuestAsync("Bob");
        var carol = await GuestAsync("Carol");
        using (alice)
        using (bob)
        using (carol)
        {
            Assert.Equal(0, (await ProfileAsync(alice)).Xp);

            var room = await PlayAsync("chog-life", alice, bob, carol);

            Assert.Equal((20, 15, 10), ((await ProfileAsync(alice)).Xp, (await ProfileAsync(bob)).Xp, (await ProfileAsync(carol)).Xp));
            var awards = await AwardsAsync();
            Assert.Equal(3, awards.Length);
            Assert.All(awards, a => Assert.Equal((room.MatchId, "chog-life", _clock.GetUtcNow(), 1), (a.RoomId, a.GameType, a.EarnedAt, a.RulesVersion)));
            Assert.Equal([(2, true, 20), (1, false, 15), (0, false, 10)], awards.OrderByDescending(a => a.Xp).Select(a => (a.OpponentsBeaten, a.Won, a.Xp)));

            await PlayAsync("chog-race", bob, alice);

            Assert.Equal((30, 30, 10), ((await ProfileAsync(alice)).Xp, (await ProfileAsync(bob)).Xp, (await ProfileAsync(carol)).Xp));
        }
    }

    [PostgresFact]
    public async Task Others_see_a_players_uploaded_photo_on_the_board()
    {
        var alice = await GuestAsync("Alice");
        var bob = await GuestAsync("Bob");
        using (alice)
        using (bob)
        {
            await PlayAsync("chog-life", alice, bob);
            using var surface = SKSurface.Create(new SKImageInfo(64, 64));
            using var png = surface.Snapshot().Encode(SKEncodedImageFormat.Png, 100);
            var photo = new ByteArrayContent(png.ToArray());
            photo.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            using (var accepted = await alice.PostAsJsonAsync("/api/profile/terms", new { version = ChogGames.Core.Players.TermsOfUse.CurrentVersion }))
            {
                Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
            }

            using (var uploaded = await alice.PutAsync("/api/profile/avatar-image", photo))
            {
                Assert.Equal(HttpStatusCode.OK, uploaded.StatusCode);
            }

            var board = await BoardAsync(bob);

            var url = board.Entries[0].Avatar.ImageUrl;
            Assert.Equal(("Alice", "classic"), (board.Entries[0].DisplayName, board.Entries[0].Avatar.Id));
            Assert.Equal(url, (await alice.GetFromJsonAsync<JsonElement>("/api/profile")).GetProperty("avatar").GetProperty("imageUrl").GetString());
            Assert.Null(board.Entries[1].Avatar.ImageUrl);
            using var seen = await bob.GetAsync(url);
            Assert.Equal(HttpStatusCode.OK, seen.StatusCode);
            Assert.Equal("image/webp", seen.Content.Headers.ContentType!.MediaType);

            // Hidden by reports (DEC-070): only its owner still sees it on the board.
            await using (var db = database.CreateDbContext())
            {
                await db.Database.ExecuteSqlRawAsync("UPDATE \"AvatarImages\" SET \"HiddenAt\" = \"CreatedAt\"");
            }

            Assert.Null((await BoardAsync(bob)).Entries[0].Avatar.ImageUrl);
            Assert.Equal(url, (await BoardAsync(alice)).Entries[0].Avatar.ImageUrl);
        }
    }

    [PostgresFact]
    public async Task Players_who_left_earn_nothing_but_count_as_beaten_and_abandoned_matches_earn_nothing()
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

            var room = await StartedAsync("chog-race", alice, bob, carol);
            (await carol.PostAsync($"/api/rooms/{room.Code}/leave", null)).Dispose();
            (await SendAsync(alice, await ViewAsync(alice, room.Code), "WIN")).Dispose();

            Assert.Equal((20, 15, 0), ((await ProfileAsync(alice)).Xp, (await ProfileAsync(bob)).Xp, (await ProfileAsync(carol)).Xp));
            Assert.Equal(1, (await ProfileAsync(carol)).GamesPlayed); // the forfeit still counts as played
            Assert.Equal(2, (await AwardsAsync()).Length);
            Assert.Null((await BoardAsync(carol)).You);
            Assert.DoesNotContain("Carol", (await BoardAsync(alice)).Entries.Select(e => e.DisplayName));
        }
    }

    [PostgresFact]
    public async Task Shared_places_beat_nobody_they_share_with()
    {
        var alice = await GuestAsync("Alice");
        var bob = await GuestAsync("Bob");
        var carol = await GuestAsync("Carol");
        using (alice)
        using (bob)
        using (carol)
        {
            var room = await StartedAsync("chog-life", alice, bob, carol);

            (await SendAsync(alice, room, "SHARED_WIN")).Dispose();

            Assert.Equal((15, 15, 10), ((await ProfileAsync(alice)).Xp, (await ProfileAsync(bob)).Xp, (await ProfileAsync(carol)).Xp));
            Assert.Equal(2, (await AwardsAsync()).Count(a => a.Won));
        }
    }

    [PostgresFact]
    public async Task Retried_and_concurrent_finishing_commands_award_XP_once()
    {
        var alice = await GuestAsync("Alice");
        var bob = await GuestAsync("Bob");
        using (alice)
        using (bob)
        {
            var room = await StartedAsync("chog-life", alice, bob);
            var actionId = Guid.NewGuid();
            var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => SendAsync(alice, room, "WIN", i % 2 == 0 ? actionId : null)));
            Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK && !Replayed(r));
            var awarded = await AwardsAsync();

            using var retry = await SendAsync(alice, room, "WIN", actionId);

            Assert.Equal(awarded, await AwardsAsync());
            Assert.Equal(2, awarded.Length);
            Assert.Equal((15, 10), ((await ProfileAsync(alice)).Xp, (await ProfileAsync(bob)).Xp));
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }

        static bool Replayed(HttpResponseMessage response) =>
            response.Content.ReadFromJsonAsync<JsonElement>(Json).Result.GetProperty("replayed").GetBoolean();
    }

    [PostgresFact]
    public async Task Changed_XP_settings_apply_to_later_matches_only_with_their_version()
    {
        var alice = await GuestAsync("Alice");
        var bob = await GuestAsync("Bob");
        using (alice)
        using (bob)
        {
            await PlayAsync("chog-life", alice, bob);
            var before = await AwardsAsync();
            var v2 = Factory(new Dictionary<string, string?>
            {
                ["Progression:Xp:Version"] = "2",
                ["Progression:Xp:PerMatch"] = "20",
                ["Progression:Xp:PerOpponentBeaten"] = "8",
            });
            using var alice2 = On(v2, alice);
            using var bob2 = On(v2, bob);

            await PlayAsync("chog-life", bob2, alice2);

            var awards = await AwardsAsync();
            Assert.All(before, a => Assert.Contains(a, awards));
            Assert.Equal([(2, 28), (2, 20)], awards.Except(before).OrderByDescending(a => a.Xp).Select(a => (a.RulesVersion, a.Xp)));
            Assert.Equal((35, 38), ((await ProfileAsync(alice)).Xp, (await ProfileAsync(bob)).Xp));
        }
    }

    [Theory]
    [InlineData("0", "10", "5")]
    [InlineData("1", "-1", "5")]
    [InlineData("1", "10", "1001")]
    public void Invalid_XP_settings_stop_startup(string version, string perMatch, string perOpponentBeaten)
    {
        var error = StartupValidation.Failure(new Dictionary<string, string?>
        {
            ["Progression:Xp:Version"] = version,
            ["Progression:Xp:PerMatch"] = perMatch,
            ["Progression:Xp:PerOpponentBeaten"] = perOpponentBeaten,
        });

        Assert.Contains("Progression:Xp", error.Message);
    }

    [PostgresFact]
    public async Task The_board_ranks_by_XP_then_wins_then_who_reached_it_first_and_never_shares_a_place()
    {
        var names = new[] { "Alice", "Bob", "Carol", "Dave", "Erin", "Gina" };
        var players = new Dictionary<string, HttpClient>();
        foreach (var name in names)
        {
            players[name] = await GuestAsync(name);
        }

        try
        {
            await PlayAsync("chog-life", players["Alice"], players["Bob"]); // Alice 15, Bob 10
            await PlayAsync("chog-race", players["Alice"], players["Bob"]); // Alice 30 (2 wins), Bob 20
            await PlayAsync("chog-life", players["Carol"], players["Dave"], players["Erin"]); // Carol 20, Dave 15, Erin 10
            await PlayAsync("chog-race", players["Dave"], players["Carol"]); // Dave 30 (1 win), Carol 30 (1 win), same match
            await PlayAsync("chog-life", players["Gina"], players["Erin"]); // Gina 15, Erin 20 after Bob

            var board = await BoardAsync(players["Erin"]);

            Assert.Equal(("all", "all-time", (SeasonDto?)null), (board.Game, board.Period, board.Season));
            Assert.Equal([1, 2, 3, 4, 5, 6], board.Entries.Select(e => e.Place));
            Assert.Equal(("Alice", 30, 2), board.Places[0]);
            Assert.Equal(["Carol", "Dave"], board.Places[1..3].Select(p => p.Name).Order());
            Assert.All(board.Places[1..3], p => Assert.Equal((30, 1), (p.Xp, p.Wins)));
            Assert.Equal([("Bob", 20, 0), ("Erin", 20, 0), ("Gina", 15, 1)], board.Places[3..]);
            Assert.Equal(new EntryDto(5, "Erin", new AvatarDto("classic", "Classic", "#5b2bd6"), 20, 0, true), board.You);
            Assert.Single(board.Entries, e => e.IsYou);

            // Players with the same XP, wins and time keep their order on every request and for every viewer.
            Assert.Equal(board.Places, (await BoardAsync(players["Alice"])).Places);
            Assert.Equal(board.Places, (await BoardAsync(players["Carol"])).Places);
        }
        finally
        {
            foreach (var client in players.Values)
            {
                client.Dispose();
            }
        }
    }

    [PostgresFact]
    public async Task Game_boards_count_only_their_game_and_season_boards_only_this_month()
    {
        var alice = await GuestAsync("Alice");
        var bob = await GuestAsync("Bob");
        using (alice)
        using (bob)
        {
            await PlayAsync("chog-life", alice, bob); // September
            _clock.Advance(TimeSpan.FromHours(1));
            await PlayAsync("chog-race", bob, alice); // October
            await PlayAsync("chog-race", bob, alice);

            Assert.Equal([("Bob", 40, 2), ("Alice", 35, 1)], (await BoardAsync(alice)).Places);
            Assert.Equal([("Alice", 15, 1), ("Bob", 10, 0)], (await BoardAsync(alice, "?game=chog-life")).Places);
            Assert.Equal([("Bob", 30, 2), ("Alice", 20, 0)], (await BoardAsync(alice, "?game=chog-race&period=all-time")).Places);

            var season = await BoardAsync(alice, "?period=season");
            Assert.Equal(("all", "season"), (season.Game, season.Period));
            Assert.Equal(new SeasonDto(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero)), season.Season);
            Assert.Equal([("Bob", 30, 2), ("Alice", 20, 0)], season.Places);
            Assert.Empty((await BoardAsync(alice, "?game=chog-life&period=season")).Entries);
            Assert.Null((await BoardAsync(alice, "?game=chog-life&period=season")).You);

            _clock.Advance(TimeSpan.FromDays(31)); // November: a new season with nothing in it yet
            Assert.Empty((await BoardAsync(alice, "?period=season")).Entries);
            Assert.Equal(2, (await BoardAsync(alice)).Entries.Length);
        }
    }

    [PostgresFact]
    public async Task A_board_lists_the_top_50_and_the_viewers_own_place_below_them()
    {
        var players = new List<HttpClient>();
        for (var i = 0; i < 52; i++)
        {
            players.Add(await GuestAsync($"Player {i:00}"));
        }

        using var newcomer = await GuestAsync("Newcomer");
        try
        {
            for (var i = 0; i < 52; i += 2)
            {
                await PlayAsync("chog-life", players[i], players[i + 1]); // 26 winners on 15, then 26 on 10
            }

            var last = await BoardAsync(players[51]);

            Assert.Equal(50, last.Entries.Length);
            Assert.Equal(Enumerable.Range(1, 50), last.Entries.Select(e => e.Place));
            Assert.Equal(new EntryDto(52, "Player 51", new AvatarDto("classic", "Classic", "#5b2bd6"), 10, 0, true), last.You);
            Assert.DoesNotContain(last.Entries, e => e.IsYou);
            Assert.Equal(("Player 00", 15), (last.Entries[0].DisplayName, last.Entries[0].Xp));
            Assert.Equal(("Player 01", 10), (last.Entries[26].DisplayName, last.Entries[26].Xp));

            var first = await BoardAsync(players[0]);
            Assert.True(first.Entries[0].IsYou);
            Assert.Equal(first.Entries[0], first.You);

            var none = await BoardAsync(newcomer);
            Assert.Null(none.You);
            Assert.DoesNotContain(none.Entries, e => e.IsYou);
        }
        finally
        {
            foreach (var client in players)
            {
                client.Dispose();
            }
        }
    }

    [PostgresFact]
    public async Task Boards_return_no_player_ids_or_wallets_and_refuse_unknown_games_periods_and_anonymous_callers()
    {
        var alice = await GuestAsync("Alice");
        var bob = await GuestAsync("Bob");
        using (alice)
        using (bob)
        using (var anonymous = Browser())
        {
            await PlayAsync("chog-life", alice, bob);
            var awards = await AwardsAsync();

            var raw = await alice.GetStringAsync("/api/leaderboards");
            Assert.DoesNotContain("playerId", raw, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("wallet", raw, StringComparison.OrdinalIgnoreCase);
            Assert.All(awards, a => Assert.DoesNotContain(a.PlayerId.ToString(), raw, StringComparison.OrdinalIgnoreCase));
            Assert.All(awards, a => Assert.DoesNotContain(a.RoomId.ToString(), raw, StringComparison.OrdinalIgnoreCase));

            foreach (var (query, code) in new[]
            {
                ("?game=chog-poker", "unknown_game"),
                ("?game=", "unknown_game"),
                ("?game=CHOG-LIFE", "unknown_game"),
                ("?period=week", "unknown_period"),
                ("?period=Season", "unknown_period"),
            })
            {
                using var refused = await alice.GetAsync($"/api/leaderboards{query}");
                Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
                Assert.Equal(code, (await refused.Content.ReadFromJsonAsync<ProblemDto>(Json))!.Code);
            }

            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/leaderboards")).StatusCode);
            using var post = await alice.PostAsJsonAsync("/api/leaderboards", new { xp = 1000 });
            Assert.Equal(HttpStatusCode.MethodNotAllowed, post.StatusCode);
            using var patch = await alice.PatchAsJsonAsync("/api/profile", new { displayName = "Alice", xp = 1000 });
            Assert.Equal(HttpStatusCode.OK, patch.StatusCode);
            Assert.Equal(awards, await AwardsAsync());
            Assert.Equal(15, (await ProfileAsync(alice)).Xp);
        }
    }

    [PostgresFact]
    public async Task Entries_show_the_collector_badge_but_never_the_chogs_held()
    {
        var alice = await GuestAsync("Alice");
        var bob = await GuestAsync("Bob");
        using (alice)
        using (bob)
        {
            await PlayAsync("chog-life", alice, bob);
            await using (var db = database.CreateDbContext())
            {
                // As a gallery read of a wallet holding 7 Chogs would record (DEC-065).
                await db.Database.ExecuteSqlRawAsync("""
                    UPDATE "Players" SET "Kind" = 'Wallet', "WalletAddress" = '0x' || repeat('a', 40), "ChogsHeld" = 7
                    WHERE "DisplayName" = 'Alice'
                    """);
            }

            var board = await BoardAsync(bob);
            var raw = await bob.GetStringAsync("/api/leaderboards");

            Assert.Equal(
                new (string, BadgeDto?)[] { ("Alice", new BadgeDto("patron", "Patron")), ("Bob", null) },
                board.Entries.Select(e => (e.DisplayName, e.Collector)));
            Assert.Equal(new BadgeDto("patron", "Patron"), (await BoardAsync(alice)).You!.Collector);
            Assert.DoesNotContain("chogsHeld", raw, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("aaaaaaaa", raw, StringComparison.OrdinalIgnoreCase);
        }
    }

    [PostgresFact]
    public async Task The_migration_backfill_awards_exactly_what_the_matches_awarded_live()
    {
        var alice = await GuestAsync("Alice");
        var bob = await GuestAsync("Bob");
        var carol = await GuestAsync("Carol");
        var dave = await GuestAsync("Dave");
        using (alice)
        using (bob)
        using (carol)
        using (dave)
        {
            await PlayAsync("chog-life", alice, bob);
            await PlayAsync("chog-race", carol, alice, dave, bob);
            var shared = await StartedAsync("chog-life", bob, carol, dave);
            (await SendAsync(bob, shared, "SHARED_WIN")).Dispose();
            var left = await StartedAsync("chog-race", dave, carol, alice);
            (await alice.PostAsync($"/api/rooms/{left.Code}/leave", null)).Dispose();
            (await SendAsync(dave, await ViewAsync(dave, left.Code), "WIN")).Dispose();
            var abandoned = await StartedAsync("chog-life", alice, bob);
            (await bob.PostAsync($"/api/rooms/{abandoned.Code}/leave", null)).Dispose();
            var live = await AwardsAsync();
            Assert.Equal(11, live.Length);

            await using (var db = database.CreateDbContext())
            {
                await db.Database.ExecuteSqlRawAsync("DELETE FROM \"XpAwards\"");
                await db.Database.ExecuteSqlRawAsync(AddXpAwards.BackfillSql);
                await db.Database.ExecuteSqlRawAsync(AddXpAwards.BackfillSql); // a second run adds nothing
            }

            Assert.Equal(live, await AwardsAsync());
        }
    }

    [PostgresFact]
    public async Task The_table_refuses_duplicate_out_of_range_and_orphan_awards()
    {
        var alice = await GuestAsync("Alice");
        var bob = await GuestAsync("Bob");
        using (alice)
        using (bob)
        {
            await PlayAsync("chog-life", alice, bob);
            var award = (await AwardsAsync()).First();
            await using var db = database.CreateDbContext();

            Task InsertAsync(Guid roomId, Guid playerId, string? gameType = "chog-life", int beaten = 0, int xp = 10, int version = 1) =>
                db.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                    INSERT INTO "XpAwards" ("RoomId", "PlayerId", "GameType", "EarnedAt", "OpponentsBeaten", "Won", "Xp", "RulesVersion")
                    VALUES ({roomId}, {playerId}, {gameType}, now(), {beaten}, false, {xp}, {version})
                    """);

            async Task RefusedAsync(string sqlState, Func<Task> insert) =>
                Assert.Equal(sqlState, (await Assert.ThrowsAsync<PostgresException>(insert)).SqlState);

            await RefusedAsync(PostgresErrorCodes.UniqueViolation, () => InsertAsync(award.RoomId, award.PlayerId));
            var other = Guid.NewGuid();
            await RefusedAsync(PostgresErrorCodes.ForeignKeyViolation, () => InsertAsync(award.RoomId, other));
            await RefusedAsync(PostgresErrorCodes.ForeignKeyViolation, () => InsertAsync(Guid.NewGuid(), award.PlayerId));

            // A fresh room for the other refusals, so only the checked column is wrong.
            await db.Database.ExecuteSqlRawAsync("DELETE FROM \"XpAwards\"");
            await RefusedAsync(PostgresErrorCodes.CheckViolation, () => InsertAsync(award.RoomId, award.PlayerId, xp: -1));
            await RefusedAsync(PostgresErrorCodes.CheckViolation, () => InsertAsync(award.RoomId, award.PlayerId, xp: 6001));
            await RefusedAsync(PostgresErrorCodes.CheckViolation, () => InsertAsync(award.RoomId, award.PlayerId, beaten: -1));
            await RefusedAsync(PostgresErrorCodes.CheckViolation, () => InsertAsync(award.RoomId, award.PlayerId, beaten: 6));
            await RefusedAsync(PostgresErrorCodes.CheckViolation, () => InsertAsync(award.RoomId, award.PlayerId, version: 0));
            await RefusedAsync(PostgresErrorCodes.NotNullViolation, () => InsertAsync(award.RoomId, award.PlayerId, gameType: null));
            await InsertAsync(award.RoomId, award.PlayerId, beaten: 5, xp: 6000);
        }
    }

    /// <summary>
    /// Ends the match at once: <c>WIN</c> ranks the sender first and the others after in seat order; <c>SHARED_WIN</c>
    /// ranks the first two seats first together and the rest third.
    /// </summary>
    private sealed class FinishingRules(string gameType) : IMatchRules
    {
        public string GameType => gameType;

        public MatchRuleDecision Apply(MatchCommandContext context)
        {
            var playing = context.Match.Members.Where(m => !m.HasLeft).OrderBy(m => m.Seat).Select(m => m.PlayerId).ToList();
            var others = playing.Where(id => id != context.PlayerId).ToList();
            var state = JsonSerializer.SerializeToElement(new { over = true });
            return context.Type switch
            {
                "WIN" => MatchRuleDecision.Finish(state, [new(context.PlayerId, 1), .. others.Select((id, i) => new MatchStanding(id, i + 2))]),
                "SHARED_WIN" => MatchRuleDecision.Finish(state, [.. playing.Select((id, i) => new MatchStanding(id, i < 2 ? 1 : 3))]),
                _ => MatchRuleDecision.Unknown,
            };
        }

        public JsonElement? TimeOutTurn(MatchTurnContext context) => null;

        public JsonElement? ViewFor(MatchViewContext context) => context.State;
    }
}
