using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Channels;
using ChogGames.Api.Matches;
using ChogGames.Api.Realtime;
using ChogGames.Core.Matches;
using ChogGames.Games.ChogLife;
using ChogGames.Games.ChogLife.Rules;
using ChogGames.Infrastructure.Matches;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ChogGames.Api.Tests.ChogLife;

/// <summary>
/// STORY-044, DEC-047: whole Chog Life matches of 2, 4 and 6 real players through the API, each with a live hub
/// connection, on seeded server chance: rolls and lane choices over REST, a player who lets two turns run out and comes
/// back, a player who reloads mid-match (finding the room with <c>/api/rooms/current</c> and resyncing over the hub),
/// and, with four or more, a player who leaves. Out-of-turn and outsider commands are tried on every step and change
/// nothing. Each match reaches the finish with no stuck turn, under rules version 2, with one result that every player
/// sees the same, over REST and live.
/// </summary>
public sealed class ChogLifeMultiplayerTests(MigratedPostgresDatabase database) : IClassFixture<MigratedPostgresDatabase>, IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static readonly TimeSpan TurnTimeout = TimeSpan.FromSeconds(25);

    /// <summary>More steps than a match of six can take; reaching it means a turn is stuck.</summary>
    private const int MaxSteps = 400;

    private readonly List<IAsyncDisposable> _disposables = [];

    private readonly ManualTimeProvider _clock = new(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));

    private ApiFactory _factory = null!;

    /// <summary>The server's chance, seeded so a failing match replays; seats are drawn from it too.</summary>
    private sealed class SeededRandomSource(int seed) : IRandomSource
    {
        private readonly Random _random = new(seed);
        private readonly object _lock = new();

        public int Next(int minInclusive, int maxExclusive)
        {
            lock (_lock)
            {
                return _random.Next(minInclusive, maxExclusive);
            }
        }
    }

    public Task InitializeAsync() => Task.CompletedTask;

    private async Task StartServerAsync(int seed)
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
                ["Matches:TurnTimeout"] = TurnTimeout.ToString(),

                // Every step reads the room and tries refused commands, far more than a real player sends in a minute.
                ["Rooms:RequestsPerMinutePerPlayer"] = "1000",
            },
            services =>
            {
                services.AddSingleton<IRandomSource>(new SeededRandomSource(seed));
                services.AddSingleton<TimeProvider>(_clock);
                foreach (var worker in services.Where(d => d.ImplementationType == typeof(TurnTimeoutWorker)).ToList())
                {
                    services.Remove(worker);
                }
            });
    }

    public async Task DisposeAsync()
    {
        foreach (var disposable in Enumerable.Reverse(_disposables))
        {
            await disposable.DisposeAsync();
        }

        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }
    }

    /// <summary>A browser with a guest session.</summary>
    private sealed record Guest(string Name, HttpClient Client, string Cookie);

    private HttpClient Browser(string cookie)
    {
        var client = _factory.CreateClient(
            new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), HandleCookies = false });
        _disposables.Add(new DisposableClient(client));
        client.DefaultRequestHeaders.Add("Cookie", cookie);
        return client;
    }

    private async Task<Guest> GuestAsync(string name)
    {
        var client = _factory.CreateClient(
            new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), HandleCookies = false });
        _disposables.Add(new DisposableClient(client));
        using var response = await client.PostAsJsonAsync("/api/guest-sessions", new { displayName = name });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var cookie = response.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
        client.DefaultRequestHeaders.Add("Cookie", cookie);
        return new Guest(name, client, cookie);
    }

    private sealed class DisposableClient(HttpClient client) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            client.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>One live connection and the snapshots it receives.</summary>
    private sealed class Listener(HubConnection connection) : IAsyncDisposable
    {
        private readonly Channel<JsonElement> _snapshots = Channel.CreateUnbounded<JsonElement>();

        public void Listen() => connection.On<JsonElement>(nameof(IRoomClient.RoomUpdated), room => _snapshots.Writer.TryWrite(room));

        public Task StartAsync() => connection.StartAsync();

        public Task<JsonElement> SubscribeAsync(string code) => connection.InvokeAsync<JsonElement>(nameof(RoomHub.SubscribeRoom), code);

        /// <summary>Snapshots until one of at least <paramref name="version"/> arrives; the newest wins (DEC-028).</summary>
        public async Task<JsonElement> UntilAsync(long version)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (true)
            {
                var room = await _snapshots.Reader.ReadAsync(timeout.Token);
                if (room.GetProperty("version").GetInt64() >= version)
                {
                    return room;
                }
            }
        }

        public ValueTask DisposeAsync() => connection.DisposeAsync();
    }

    private async Task<Listener> ConnectAsync(string cookie, string code)
    {
        var server = _factory.Server;
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri($"https://localhost{RoomHub.Path}"), HttpTransportType.WebSockets, options =>
            {
                options.SkipNegotiation = true;
                options.WebSocketFactory = async (context, cancellationToken) =>
                {
                    var client = server.CreateWebSocketClient();
                    client.ConfigureRequest = request => request.Headers.Cookie = cookie;
                    return await client.ConnectAsync(context.Uri, cancellationToken);
                };
            })
            .Build();
        var listener = new Listener(connection);
        _disposables.Add(listener);
        listener.Listen();
        await listener.StartAsync();
        await listener.SubscribeAsync(code);
        return listener;
    }

    private static async Task<JsonElement> SendAsync(HttpClient client, HttpMethod method, string path, object? body = null, HttpStatusCode expected = HttpStatusCode.OK)
    {
        using var request = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body) };
        using var response = await client.SendAsync(request);
        Assert.Equal(expected, response.StatusCode);
        return response.Content.Headers.ContentLength == 0 ? default : await response.Content.ReadFromJsonAsync<JsonElement>(Json);
    }

    private static Task<JsonElement> ViewAsync(Guest guest, string code) => SendAsync(guest.Client, HttpMethod.Get, $"/api/rooms/{code}");

    private async Task<string> StartedAsync(Guest[] players)
    {
        var created = await SendAsync(players[0].Client, HttpMethod.Post, "/api/rooms", new { gameType = "chog-life" }, HttpStatusCode.Created);
        var code = created.GetProperty("code").GetString()!;
        foreach (var player in players[1..])
        {
            _clock.Advance(TimeSpan.FromSeconds(1));
            await SendAsync(player.Client, HttpMethod.Post, $"/api/rooms/{code}/join");
        }

        foreach (var player in players)
        {
            await SendAsync(player.Client, HttpMethod.Put, $"/api/rooms/{code}/ready", new { ready = true });
        }

        await SendAsync(players[0].Client, HttpMethod.Post, $"/api/rooms/{code}/start");
        return code;
    }

    private static Task<JsonElement> CommandAsync(Guest guest, JsonElement room, string type, object? payload, HttpStatusCode expected = HttpStatusCode.OK) =>
        SendAsync(guest.Client, HttpMethod.Post, $"/api/rooms/{room.GetProperty("code").GetString()}/commands", new
        {
            actionId = Guid.NewGuid(),
            matchId = room.GetProperty("matchId").GetGuid(),
            expectedVersion = room.GetProperty("version").GetInt64(),
            type,
            payload,
        }, expected);

    private async Task SweepAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<TurnTimeoutService>().TimeOutOverdueTurnsAsync(CancellationToken.None);
    }

    private static JsonElement Member(JsonElement room, string name) =>
        room.GetProperty("members").EnumerateArray().Single(m => m.GetProperty("displayName").GetString() == name);

    private static string? TurnOf(JsonElement room) =>
        room.GetProperty("members").EnumerateArray().SingleOrDefault(m => m.GetProperty("isTurn").GetBoolean()) is { ValueKind: JsonValueKind.Object } member
            ? member.GetProperty("displayName").GetString()
            : null;

    private static int SeatOf(JsonElement room, string name) => Member(room, name).GetProperty("seat").GetInt32();

    /// <summary>What every player must see alike: the board and each member's seat, rank, result and whether they left.</summary>
    private static string Shared(JsonElement room) => JsonSerializer.Serialize(new
    {
        status = room.GetProperty("status").GetString(),
        version = room.GetProperty("version").GetInt64(),
        state = room.GetProperty("state"),
        members = room.GetProperty("members").EnumerateArray().Select(m => new
        {
            name = m.GetProperty("displayName").GetString(),
            seat = m.GetProperty("seat"),
            hasLeft = m.GetProperty("hasLeft").GetBoolean(),
            rank = m.TryGetProperty("rank", out var rank) ? rank.GetRawText() : null,
            result = m.TryGetProperty("result", out var result) ? result.GetRawText() : null,
        }),
    });

    [PostgresTheory]
    [InlineData(2, 11)]
    [InlineData(4, 12)]
    [InlineData(6, 13)]
    public async Task A_whole_match_reaches_the_finish_with_an_afk_player_a_reload_and_a_leaver(int count, int seed)
    {
        await StartServerAsync(seed);
        var names = new[] { "Alice", "Bob", "Carol", "Dave", "Erin", "Frank" }[..count];
        var players = new List<Guest>();
        foreach (var name in names)
        {
            players.Add(await GuestAsync(name));
        }

        var outsider = await GuestAsync("Mallory");
        var code = await StartedAsync([.. players]);
        var listeners = new Dictionary<string, Listener>();
        foreach (var player in players)
        {
            listeners[player.Name] = await ConnectAsync(player.Cookie, code);
        }

        var afk = players[1];
        var reloader = players[^1];
        var leaver = count >= 4 ? players[2] : null;
        var missed = 0;
        var reloaded = false;
        var lanes = 0;
        var step = 0;
        var room = await ViewAsync(players[0], code);
        Assert.Equal(LifeRulesV2.Version, room.GetProperty("state").GetProperty("rulesVersion").GetInt32());
        while (room.GetProperty("status").GetString() == "Active")
        {
            Assert.True(++step < MaxSteps, $"The match is stuck after {step} steps.");
            var version = room.GetProperty("version").GetInt64();
            var turn = TurnOf(room)!;

            // Someone not on turn, and someone outside the match, try to roll; nothing changes (§21).
            var waiting = players.First(p => p.Name != turn && !Member(room, p.Name).GetProperty("hasLeft").GetBoolean());
            await CommandAsync(waiting, room, ChogLifeMatchRules.Roll, null, HttpStatusCode.Conflict);
            await CommandAsync(outsider, room, ChogLifeMatchRules.Roll, null, HttpStatusCode.NotFound);
            Assert.Equal(version, (await ViewAsync(players[0], code)).GetProperty("version").GetInt64());

            if (leaver is not null && step == 12 && !Member(room, leaver.Name).GetProperty("hasLeft").GetBoolean())
            {
                await SendAsync(leaver.Client, HttpMethod.Post, $"/api/rooms/{code}/leave", expected: HttpStatusCode.NoContent);
            }
            else if (!reloaded && step == 8)
            {
                // A browser refresh: a new client with only the cookie finds the room, and a new connection resyncs.
                await listeners[reloader.Name].DisposeAsync();
                var browser = Browser(reloader.Cookie);
                var current = await SendAsync(browser, HttpMethod.Get, "/api/rooms/current");
                Assert.Equal(code, current.GetProperty("code").GetString());
                listeners[reloader.Name] = await ConnectAsync(reloader.Cookie, code);
                var resynced = await listeners[reloader.Name].SubscribeAsync(code);
                Assert.Equal(Shared(current), Shared(resynced));
                players[^1] = reloader with { Client = browser };
                reloaded = true;
                continue;
            }
            else if (turn == afk.Name && missed < 2)
            {
                // The player is away: their turn runs out and the timer passes it on; two misses don't forfeit them.
                _clock.Advance(room.GetProperty("turnEndsAt").GetDateTimeOffset() - _clock.GetUtcNow() + TimeSpan.FromSeconds(1));
                await SweepAsync();
                missed++;
            }
            else
            {
                var player = players.Single(p => p.Name == turn);
                var state = room.GetProperty("state");
                if (state.TryGetProperty("choosing", out var choosing) && choosing.GetInt32() == SeatOf(room, turn))
                {
                    await CommandAsync(player, room, ChogLifeMatchRules.ChooseLane, new { lane = lanes++ % 2 == 0 ? "Safe" : "Degen" });
                }
                else
                {
                    await CommandAsync(player, room, ChogLifeMatchRules.Roll, null);
                }

                _clock.Advance(TimeSpan.FromSeconds(3));
            }

            room = await ViewAsync(players[0], code);
            Assert.True(room.GetProperty("version").GetInt64() > version, $"Step {step} changed nothing.");
        }

        Assert.Equal("Completed", room.GetProperty("status").GetString());
        Assert.Equal("Finish", room.GetProperty("state").GetProperty("ended").GetString());
        Assert.Equal(2, missed);
        Assert.True(reloaded);
        Assert.False(Member(room, afk.Name).GetProperty("hasLeft").GetBoolean());

        // One result: the same for every player over REST and live, the first to finish holding version 2's bonus.
        var final = Shared(room);
        var finalVersion = room.GetProperty("version").GetInt64();
        foreach (var player in players)
        {
            Assert.Equal(final, Shared(await ViewAsync(player, code)));
            if (player != leaver)
            {
                Assert.Equal(final, Shared(await listeners[player.Name].UntilAsync(finalVersion)));
            }
        }

        var stillPlaying = room.GetProperty("members").EnumerateArray().Where(m => !m.GetProperty("hasLeft").GetBoolean()).ToArray();
        Assert.Equal(count - (leaver is null ? 0 : 1), stillPlaying.Length);
        Assert.Contains(stillPlaying, m => m.GetProperty("rank").GetInt32() == 1);
        var first = stillPlaying.Single(m => m.GetProperty("result").GetProperty("place").GetInt32() == 1);
        Assert.Equal(LifeRulesV2.FinishBonus, first.GetProperty("result").GetProperty("bonuses").GetInt32());
        if (leaver is not null)
        {
            var left = Member(room, leaver.Name);
            Assert.True(left.GetProperty("hasLeft").GetBoolean());
            Assert.False(left.TryGetProperty("result", out var none) && none.ValueKind != JsonValueKind.Null);
        }

        await using var db = database.CreateDbContext();
        var stats = await db.Players.AsNoTracking().Where(p => names.Contains(p.DisplayName)).ToListAsync();
        Assert.All(stats, p => Assert.Equal(1, p.GamesPlayed));
        Assert.Equal(stillPlaying.Count(m => m.GetProperty("rank").GetInt32() == 1), stats.Sum(p => p.Wins));
    }
}
