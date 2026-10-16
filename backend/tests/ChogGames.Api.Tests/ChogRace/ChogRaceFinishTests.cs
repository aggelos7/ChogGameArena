using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ChogGames.Api.Matches;
using ChogGames.Core.Matches;
using ChogGames.Infrastructure.Matches;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ChogGames.Api.Tests.ChogRace;

/// <summary>
/// STORY-056, RACE-008, RACE-009, DEC-060: a Chog Race match ends once, when a pawn reaches the Finish or when its
/// 20-minute cap is up. The others are ranked by tile, then by who reached it first; every player still playing gets a
/// result stored with their rank, the same for every player, and the stats are recorded once. Nobody can act once the
/// time is up.
/// </summary>
public sealed class ChogRaceFinishTests(MigratedPostgresDatabase database) : IClassFixture<MigratedPostgresDatabase>, IAsyncLifetime
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
                ["Games:Items:1:Status"] = "Available",
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
    /// A started Chog Race match of <paramref name="players"/>, seated in join order; returns its code. Joins are a second
    /// apart, so the order doesn't fall back to player ids.
    /// </summary>
    private async Task<string> StartedAsync(params Guest[] players)
    {
        var created = await SendAsync(players[0], HttpMethod.Post, "/api/rooms", new { gameType = "chog-race" }, HttpStatusCode.Created);
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

    /// <summary>Each member's result by seat, its keys sorted: jsonb reorders them.</summary>
    private static string[] Results(JsonElement room) =>
        room.GetProperty("members").EnumerateArray()
            .OrderBy(m => m.GetProperty("seat").GetInt32())
            .Select(m => m.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object ? Sorted(result) : "none")
            .ToArray();

    private static string Sorted(JsonElement result) =>
        string.Join(",", result.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal).Select(p => $"{p.Name}={p.Value.GetRawText()}"));

    private static string Sorted(string json) => Sorted(JsonDocument.Parse(json).RootElement);

    [PostgresFact]
    public async Task At_the_time_cap_the_furthest_pawn_wins_ties_go_to_who_got_there_first_and_results_are_recorded_once()
    {
        var alice = await GuestAsync("Alice");
        var bob = await GuestAsync("Bob");
        var carol = await GuestAsync("Carol");
        var code = await StartedAsync(alice, bob, carol);
        var started = await ViewAsync(alice, code);
        Assert.Equal(_clock.GetUtcNow() + TimeSpan.FromMinutes(20), started.GetProperty("matchEndsAt").GetDateTimeOffset());
        await SetStateAsync(code, """
            {"rulesVersion":1,"moves":5,"deck":["Pump"],"discard":["Rug"],"pawns":[
              {"seat":1,"tile":13,"since":5,"stats":{"cardsPlayed":1,"attacksLanded":1}},
              {"seat":2,"tile":19,"since":4},
              {"seat":3,"tile":13,"since":2,"stats":{"attacksBlocked":1}}],
             "lastMove":{"seat":1,"roll":2,"from":11,"landed":13,"to":13},"playPhase":1}
            """);
        _clock.Advance(TimeSpan.FromMinutes(20));
        var drawsBefore = _dice.Draws.Count;

        // Before the sweep gets there, nobody can act any more.
        var refused = await CommandAsync(alice, started, "END_TURN", null, HttpStatusCode.Conflict);
        Assert.Equal("time_up", refused.GetProperty("code").GetString());

        Assert.Single(await SweepAsync());
        Assert.Empty(await SweepAsync());

        foreach (var seen in new[] { await ViewAsync(alice, code), await ViewAsync(bob, code), await ViewAsync(carol, code) })
        {
            Assert.Equal("Completed", seen.GetProperty("status").GetString());
            Assert.Equal([3, 1, 2], Ranks(seen));
            Assert.Equal("TimeUp", seen.GetProperty("state").GetProperty("ended").GetString());
            Assert.False(seen.GetProperty("state").TryGetProperty("phase", out _));
            Assert.Equal(
                [
                    Sorted("""{"tile":13,"finished":false,"tilesLeft":14,"cardsPlayed":1,"attacksLanded":1,"attacksBlocked":0}"""),
                    Sorted("""{"tile":19,"finished":false,"tilesLeft":8,"cardsPlayed":0,"attacksLanded":0,"attacksBlocked":0}"""),
                    Sorted("""{"tile":13,"finished":false,"tilesLeft":14,"cardsPlayed":0,"attacksLanded":0,"attacksBlocked":1}"""),
                ],
                Results(seen));
        }

        Assert.Equal(_dice.Draws.Count, drawsBefore);
        Assert.Equal([(1, 0), (1, 1), (1, 0)], await StatsAsync("Alice", "Bob", "Carol"));
    }

    [PostgresFact]
    public async Task Reaching_the_finish_ends_the_match_once_even_when_the_command_is_retried_or_the_cap_passes()
    {
        var alice = await GuestAsync("Alice");
        var bob = await GuestAsync("Bob");
        var code = await StartedAsync(alice, bob);
        await SetStateAsync(code, """
            {"rulesVersion":1,"moves":3,"deck":["Pump"],"discard":[],"pawns":[{"seat":1,"tile":26,"since":3},{"seat":2,"tile":21,"since":2}]}
            """);
        var before = await ViewAsync(alice, code);
        var actionId = Guid.NewGuid();

        _dice.Enqueue(3);
        await RollAsync(alice, before, actionId: actionId);
        await RollAsync(alice, before, actionId: actionId);

        foreach (var seen in new[] { await ViewAsync(alice, code), await ViewAsync(bob, code) })
        {
            Assert.Equal("Completed", seen.GetProperty("status").GetString());
            Assert.Equal([1, 2], Ranks(seen));
            Assert.Equal(1, seen.GetProperty("state").GetProperty("winner").GetInt32());
            Assert.Equal("Finish", seen.GetProperty("state").GetProperty("ended").GetString());
            Assert.Equal(
                [
                    Sorted("""{"tile":27,"finished":true,"tilesLeft":0,"cardsPlayed":0,"attacksLanded":0,"attacksBlocked":0}"""),
                    Sorted("""{"tile":21,"finished":false,"tilesLeft":6,"cardsPlayed":0,"attacksLanded":0,"attacksBlocked":0}"""),
                ],
                Results(seen));
        }

        _clock.Advance(TimeSpan.FromMinutes(21));
        Assert.Empty(await SweepAsync());
        Assert.Equal([(1, 1), (1, 0)], await StatsAsync("Alice", "Bob"));
        await RollAsync(bob, await ViewAsync(bob, code), HttpStatusCode.Conflict);
    }
}
