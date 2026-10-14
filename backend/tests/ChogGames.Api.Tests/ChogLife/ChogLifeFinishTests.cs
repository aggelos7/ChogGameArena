using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ChogGames.Api.Matches;
using ChogGames.Core.Matches;
using ChogGames.Infrastructure.Matches;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ChogGames.Api.Tests.ChogLife;

/// <summary>
/// STORY-041, DEC-043: a Chog Life match ends when every player still playing has reached the Finish, or when its
/// 20-minute cap is up, with one result: players are ranked by portfolio, then finish order, then progress, the results
/// are recorded once, and every player sees the same final board. The first to arrive gets the finish bonus, finished
/// players are skipped in the turn order, and nobody can act once the time is up.
/// </summary>
public sealed class ChogLifeFinishTests(MigratedPostgresDatabase database) : IClassFixture<MigratedPostgresDatabase>, IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Empty, it keeps seats in join order; each test queues its rolls after the start.</summary>
    private readonly ScriptedRandomSource _dice = new();

    private readonly List<IAsyncDisposable> _disposables = [];

    private readonly ManualTimeProvider _clock = new(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));

    private ApiFactory _factory = null!;

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
                ["Matches:TurnTimeout"] = "00:05:00",
            },
            services =>
            {
                services.AddSingleton<IRandomSource>(_dice);
                services.AddSingleton<TimeProvider>(_clock);
                foreach (var worker in services.Where(d => d.ImplementationType == typeof(TurnTimeoutWorker)).ToList())
                {
                    services.Remove(worker);
                }
            });
    }

    public async Task DisposeAsync()
    {
        foreach (var disposable in _disposables)
        {
            await disposable.DisposeAsync();
        }

        await _factory.DisposeAsync();
    }

    /// <summary>A browser with a guest session.</summary>
    private sealed record Guest(HttpClient Client, string Cookie);

    private async Task<Guest> GuestAsync(string name)
    {
        var client = _factory.CreateClient(
            new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), HandleCookies = false });
        _disposables.Add(new DisposableClient(client));
        using var response = await client.PostAsJsonAsync("/api/guest-sessions", new { displayName = name });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var cookie = response.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
        client.DefaultRequestHeaders.Add("Cookie", cookie);
        return new Guest(client, cookie);
    }

    private sealed class DisposableClient(HttpClient client) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            client.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private static async Task<JsonElement> SendAsync(Guest guest, HttpMethod method, string path, object? body = null, HttpStatusCode expected = HttpStatusCode.OK)
    {
        using var request = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body) };
        using var response = await guest.Client.SendAsync(request);
        Assert.Equal(expected, response.StatusCode);
        return response.Content.Headers.ContentLength == 0 ? default : await response.Content.ReadFromJsonAsync<JsonElement>(Json);
    }

    private static Task<JsonElement> ViewAsync(Guest guest, string code) => SendAsync(guest, HttpMethod.Get, $"/api/rooms/{code}");

    /// <summary>
    /// A started Chog Life match of <paramref name="players"/>, seated in join order; returns its code. Joins are a second
    /// apart, so the order doesn't fall back to player ids.
    /// </summary>
    private async Task<string> StartedAsync(params Guest[] players)
    {
        var created = await SendAsync(players[0], HttpMethod.Post, "/api/rooms", new { gameType = "chog-life" }, HttpStatusCode.Created);
        var code = created.GetProperty("code").GetString()!;
        foreach (var player in players[1..])
        {
            _clock.Advance(TimeSpan.FromSeconds(1));
            await SendAsync(player, HttpMethod.Post, $"/api/rooms/{code}/join");
        }

        foreach (var player in players)
        {
            await SendAsync(player, HttpMethod.Put, $"/api/rooms/{code}/ready", new { ready = true });
        }

        await SendAsync(players[0], HttpMethod.Post, $"/api/rooms/{code}/start");
        return code;
    }

    private static Task<JsonElement> RollAsync(
        Guest guest, JsonElement room, HttpStatusCode expected = HttpStatusCode.OK, object? payload = null, Guid? actionId = null) =>
        CommandAsync(guest, room, "ROLL", payload, expected, actionId);

    private static Task<JsonElement> ChooseAsync(Guest guest, JsonElement room, object? payload, HttpStatusCode expected = HttpStatusCode.OK) =>
        CommandAsync(guest, room, "CHOOSE_LANE", payload, expected);

    private static Task<JsonElement> CommandAsync(
        Guest guest, JsonElement room, string type, object? payload, HttpStatusCode expected, Guid? actionId = null) =>
        SendAsync(guest, HttpMethod.Post, $"/api/rooms/{room.GetProperty("code").GetString()}/commands", new
        {
            actionId = actionId ?? Guid.NewGuid(),
            matchId = room.GetProperty("matchId").GetGuid(),
            expectedVersion = room.GetProperty("version").GetInt64(),
            type,
            payload,
        }, expected);

    private async Task<string?> StoredStateAsync(string code)
    {
        await using var db = database.CreateDbContext();
        var state = await db.GameRooms.AsNoTracking().Where(r => r.Code == code).Select(r => r.State).SingleAsync();
        return state is null ? null : JsonSerializer.Serialize(JsonDocument.Parse(state).RootElement);
    }

    private static int TurnSeat(JsonElement room) =>
        room.GetProperty("members").EnumerateArray().Single(m => m.GetProperty("isTurn").GetBoolean()).GetProperty("seat").GetInt32();

    private static int?[] Ranks(JsonElement room) =>
        room.GetProperty("members").EnumerateArray()
            .OrderBy(m => m.GetProperty("seat").GetInt32())
            .Select(m => m.TryGetProperty("rank", out var rank) && rank.ValueKind == JsonValueKind.Number ? rank.GetInt32() : (int?)null)
            .ToArray();

    /// <summary>Puts the match's pawns where a test needs them, as earlier rolls would have.</summary>
    private async Task SetStateAsync(string code, string state)
    {
        await using var db = database.CreateDbContext();
        await db.Database.ExecuteSqlInterpolatedAsync($"""UPDATE "GameRooms" SET "State" = {state}::jsonb WHERE "Code" = {code}""");
    }

    private async Task<IReadOnlyList<TimedOutTurn>> SweepAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<TurnTimeoutService>().TimeOutOverdueTurnsAsync(CancellationToken.None);
    }

    private async Task<(int GamesPlayed, int Wins)[]> StatsAsync(params string[] names)
    {
        await using var db = database.CreateDbContext();
        var players = await db.Players.AsNoTracking().ToListAsync();
        return names.Select(name => players.Single(p => p.DisplayName == name)).Select(p => (p.GamesPlayed, p.Wins)).ToArray();
    }

    [PostgresFact]
    public async Task A_match_ends_with_one_result_when_its_time_is_up_even_with_players_on_the_board()
    {
        var alice = await GuestAsync("Alice");
        var bob = await GuestAsync("Bob");
        var carol = await GuestAsync("Carol");
        var code = await StartedAsync(alice, bob, carol);
        var started = await ViewAsync(alice, code);
        Assert.Equal(_clock.GetUtcNow() + TimeSpan.FromMinutes(20), started.GetProperty("matchEndsAt").GetDateTimeOffset());
        await SetStateAsync(code, """
            {"rulesVersion":1,"pawns":[
              {"seat":1,"tile":9,"mon":60},
              {"seat":2,"tile":12,"mon":40,"stake":{"lane":"Safe","amount":50}},
              {"seat":3,"tile":30,"mon":90}],
             "choosing":1}
            """);
        _clock.Advance(TimeSpan.FromMinutes(20));
        var drawsBefore = _dice.Draws.Count;

        // Before the sweep gets there, nobody can act any more.
        var refused = await ChooseAsync(alice, started, new { lane = "Safe" }, HttpStatusCode.Conflict);
        Assert.Equal("time_up", refused.GetProperty("code").GetString());

        Assert.Single(await SweepAsync());
        Assert.Empty(await SweepAsync());

        foreach (var seen in new[] { await ViewAsync(alice, code), await ViewAsync(bob, code), await ViewAsync(carol, code) })
        {
            Assert.Equal("Completed", seen.GetProperty("status").GetString());
            Assert.Equal([3, 2, 1], Ranks(seen));
            Assert.Equal("TimeUp", seen.GetProperty("state").GetProperty("ended").GetString());
            Assert.False(seen.GetProperty("state").TryGetProperty("choosing", out _));
            Assert.Equal([60, 90, 90], seen.GetProperty("state").GetProperty("pawns").EnumerateArray().Select(p => p.GetProperty("portfolio").GetInt32()));
        }

        Assert.Equal(_dice.Draws.Count, drawsBefore);
        Assert.Equal([(1, 0), (1, 0), (1, 1)], await StatsAsync("Alice", "Bob", "Carol"));
    }

    [PostgresFact]
    public async Task Finished_players_are_skipped_and_the_last_arrival_ends_the_match()
    {
        var alice = await GuestAsync("Alice");
        var bob = await GuestAsync("Bob");
        var carol = await GuestAsync("Carol");
        var code = await StartedAsync(alice, bob, carol);
        await SetStateAsync(code, """
            {"rulesVersion":1,"pawns":[{"seat":1,"tile":34,"mon":100},{"seat":2,"tile":33,"mon":180},{"seat":3,"tile":20,"mon":100}]}
            """);

        _dice.Enqueue(1);
        await RollAsync(alice, await ViewAsync(alice, code));
        var afterAlice = await ViewAsync(bob, code);
        Assert.Equal("""{"seat":1,"tile":35,"mon":100,"assets":0,"bonuses":100,"liabilities":0,"portfolio":200,"place":1}""",
            afterAlice.GetProperty("state").GetProperty("pawns")[0].GetRawText());
        Assert.Equal(2, TurnSeat(afterAlice));

        _dice.Enqueue(2);
        await RollAsync(bob, afterAlice);
        var afterBob = await ViewAsync(carol, code);
        Assert.Equal(3, TurnSeat(afterBob));

        // Alice and Bob have finished, so Carol plays every turn now.
        _dice.Enqueue(1);
        await RollAsync(carol, afterBob);
        var again = await ViewAsync(carol, code);
        Assert.Equal((3, "Active"), (TurnSeat(again), again.GetProperty("status").GetString()));
        await RollAsync(alice, again, HttpStatusCode.Conflict);

        await SetStateAsync(code, (await StoredStateAsync(code))!.Replace("\"tile\":21", "\"tile\":34"));
        _dice.Enqueue(1);
        await RollAsync(carol, again);

        var ended = await ViewAsync(alice, code);
        Assert.Equal("Completed", ended.GetProperty("status").GetString());
        Assert.Equal("Finish", ended.GetProperty("state").GetProperty("ended").GetString());
        Assert.Equal([1, 2, 3], Ranks(ended));
        Assert.Equal([(1, 1), (1, 0), (1, 0)], await StatsAsync("Alice", "Bob", "Carol"));
        Assert.Empty(await SweepAsync());
    }

    [PostgresFact]
    public async Task When_the_last_player_on_the_board_leaves_the_finished_players_are_ranked()
    {
        var alice = await GuestAsync("Alice");
        var bob = await GuestAsync("Bob");
        var carol = await GuestAsync("Carol");
        var code = await StartedAsync(alice, bob, carol);
        await SetStateAsync(code, """
            {"rulesVersion":1,"pawns":[{"seat":1,"tile":34,"mon":100},{"seat":2,"tile":34,"mon":300},{"seat":3,"tile":0,"mon":100}]}
            """);
        _dice.Enqueue(1);
        await RollAsync(alice, await ViewAsync(alice, code));
        _dice.Enqueue(1);
        await RollAsync(bob, await ViewAsync(bob, code));

        await SendAsync(carol, HttpMethod.Post, $"/api/rooms/{code}/leave", expected: HttpStatusCode.NoContent);
        Assert.Single(await SweepAsync());

        var ended = await ViewAsync(alice, code);
        Assert.Equal(("Completed", "Finish"), (ended.GetProperty("status").GetString(), ended.GetProperty("state").GetProperty("ended").GetString()));
        Assert.Equal([2, 1, 3], Ranks(ended));
        Assert.Equal([(1, 0), (1, 1), (1, 0)], await StatsAsync("Alice", "Bob", "Carol"));
    }

    [PostgresFact]
    public async Task A_restart_gives_a_full_turn_but_does_not_extend_the_time_cap()
    {
        var alice = await GuestAsync("Alice");
        var bob = await GuestAsync("Bob");
        var code = await StartedAsync(alice, bob);
        var endsAt = (await ViewAsync(alice, code)).GetProperty("matchEndsAt").GetDateTimeOffset();
        _clock.Advance(TimeSpan.FromMinutes(19));

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<MatchRecoveryService>().ResumeActiveMatchesAsync(CancellationToken.None);
        }

        var resumed = await ViewAsync(alice, code);
        Assert.Equal(endsAt, resumed.GetProperty("matchEndsAt").GetDateTimeOffset());
        Assert.True(resumed.GetProperty("turnEndsAt").GetDateTimeOffset() > endsAt);

        _clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Single(await SweepAsync());
        Assert.Equal("Completed", (await ViewAsync(bob, code)).GetProperty("status").GetString());
    }
}
