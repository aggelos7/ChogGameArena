using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Channels;
using ChogGames.Api.ChogLife;
using ChogGames.Api.ChogRace;
using ChogGames.Api.Matches;
using ChogGames.Api.Realtime;
using ChogGames.Core.Abilities;
using ChogGames.Core.Matches;
using ChogGames.Games.ChogLife;
using ChogGames.Games.ChogRace;
using ChogGames.Infrastructure.Matches;
using ChogGames.Infrastructure.Rooms;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ChogGames.Api.Tests.CrossGame;

/// <summary>
/// STORY-057, DEC-061: one set of platform services serves both games. A Chog Life and a Chog Race match play to the
/// finish at the same time, with commands for both sent together, through the same room, match, session and realtime
/// services and one room hub; a player keeps one session, Chog and profile across both games, and is in one room at a
/// time whichever game it plays. Commands, match ids and subscriptions meant for the other game or room are refused
/// with nothing changed. The game modules add only their rules and ability catalogs, never a room, auth or realtime
/// service of their own.
/// </summary>
public sealed class CrossGameTests(MigratedPostgresDatabase database) : IClassFixture<MigratedPostgresDatabase>, IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>More steps than two two-player matches take; reaching it means a turn is stuck.</summary>
    private const int MaxSteps = 400;

    private readonly ManualTimeProvider _clock = new(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
    private readonly List<IAsyncDisposable> _disposables = [];
    private readonly List<ServiceDescriptor> _registrations = [];
    private ApiFactory _factory = null!;

    public async Task InitializeAsync()
    {
        if (database.ConnectionString.Length > 0)
        {
            await using var db = database.CreateDbContext();
            await db.Database.ExecuteSqlRawAsync("TRUNCATE \"GuestSessions\", \"Players\" CASCADE");
        }

        // Both games are on, as with CHOG_LIFE_STATUS and CHOG_RACE_STATUS set to Available.
        _factory = new ApiFactory(
            database.ConnectionString,
            new Dictionary<string, string?>
            {
                ["Games:Items:0:Status"] = "Available",
                ["Games:Items:1:Status"] = "Available",
                ["Matches:TurnTimeout"] = "00:05:00",

                // Every step reads both rooms, far more than a real player does in a minute.
                ["Rooms:RequestsPerMinutePerPlayer"] = "1000",
            },
            services =>
            {
                services.AddSingleton<IRandomSource>(new SeededRandomSource(57));
                services.AddSingleton<TimeProvider>(_clock);
                foreach (var worker in services.Where(d => d.ImplementationType == typeof(TurnTimeoutWorker)).ToList())
                {
                    services.Remove(worker);
                }

                _registrations.AddRange(services);
            });
    }

    public async Task DisposeAsync()
    {
        foreach (var disposable in Enumerable.Reverse(_disposables))
        {
            await disposable.DisposeAsync();
        }

        await _factory.DisposeAsync();
    }

    /// <summary>The server's chance, seeded so a failing run replays; seats are drawn from it too.</summary>
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

    /// <summary>A browser with a guest session; the cookie is kept so live connections can send it too.</summary>
    private sealed record Guest(string Name, HttpClient Client, string Cookie);

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

    /// <summary>One live connection and every snapshot it receives.</summary>
    private sealed class Listener(HubConnection connection) : IAsyncDisposable
    {
        private readonly Channel<JsonElement> _snapshots = Channel.CreateUnbounded<JsonElement>();

        public void Listen() => connection.On<JsonElement>(nameof(IRoomClient.RoomUpdated), room => _snapshots.Writer.TryWrite(room));

        public Task StartAsync() => connection.StartAsync();

        public Task<JsonElement> SubscribeAsync(string code) => connection.InvokeAsync<JsonElement>(nameof(RoomHub.SubscribeRoom), code);

        /// <summary>
        /// Snapshots until one of at least <paramref name="version"/> arrives, the newest winning (DEC-028); each must be of
        /// <paramref name="code"/>, since the connection is subscribed to that room only.
        /// </summary>
        public async Task<JsonElement> UntilAsync(string code, long version)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (true)
            {
                var room = await _snapshots.Reader.ReadAsync(timeout.Token);
                Assert.Equal(code, room.GetProperty("code").GetString());
                if (room.GetProperty("version").GetInt64() >= version)
                {
                    return room;
                }
            }
        }

        public ValueTask DisposeAsync() => connection.DisposeAsync();
    }

    private async Task<Listener> ConnectAsync(Guest guest)
    {
        var server = _factory.Server;
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri($"https://localhost{RoomHub.Path}"), HttpTransportType.WebSockets, options =>
            {
                options.SkipNegotiation = true;
                options.WebSocketFactory = async (context, cancellationToken) =>
                {
                    var client = server.CreateWebSocketClient();
                    client.ConfigureRequest = request => request.Headers.Cookie = guest.Cookie;
                    return await client.ConnectAsync(context.Uri, cancellationToken);
                };
            })
            .Build();
        var listener = new Listener(connection);
        _disposables.Add(listener);
        listener.Listen();
        await listener.StartAsync();
        return listener;
    }

    private static async Task<JsonElement> SendAsync(Guest guest, HttpMethod method, string path, object? body = null, HttpStatusCode expected = HttpStatusCode.OK)
    {
        using var request = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body) };
        using var response = await guest.Client.SendAsync(request);
        Assert.Equal(expected, response.StatusCode);
        return response.Content.Headers.ContentLength == 0 ? default : await response.Content.ReadFromJsonAsync<JsonElement>(Json);
    }

    private static Task<JsonElement> ViewAsync(Guest guest, string code) => SendAsync(guest, HttpMethod.Get, $"/api/rooms/{code}");

    /// <summary>A started match of <paramref name="gameType"/>, the first player hosting; returns its code.</summary>
    private async Task<string> StartedAsync(string gameType, params Guest[] players)
    {
        var created = await SendAsync(players[0], HttpMethod.Post, "/api/rooms", new { gameType }, HttpStatusCode.Created);
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

    private static Task<JsonElement> CommandAsync(
        Guest guest, JsonElement room, string type, object? payload = null, HttpStatusCode expected = HttpStatusCode.OK,
        string? code = null, Guid? matchId = null) =>
        SendAsync(guest, HttpMethod.Post, $"/api/rooms/{code ?? room.GetProperty("code").GetString()}/commands", new
        {
            actionId = Guid.NewGuid(),
            matchId = matchId ?? room.GetProperty("matchId").GetGuid(),
            expectedVersion = room.GetProperty("version").GetInt64(),
            type,
            payload,
        }, expected);

    private async Task<int> SweepAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        return (await scope.ServiceProvider.GetRequiredService<TurnTimeoutService>().TimeOutOverdueTurnsAsync(CancellationToken.None)).Count;
    }

    private static JsonElement Member(JsonElement room, string name) =>
        room.GetProperty("members").EnumerateArray().Single(m => m.GetProperty("displayName").GetString() == name);

    private static int SeatOf(JsonElement room, string name) => Member(room, name).GetProperty("seat").GetInt32();

    private static string TurnOf(JsonElement room) =>
        room.GetProperty("members").EnumerateArray().Single(m => m.GetProperty("isTurn").GetBoolean()).GetProperty("displayName").GetString()!;

    /// <summary>
    /// The next move a player makes in <paramref name="room"/>: in Chog Life a lane when they are at a fork, otherwise a roll;
    /// in Chog Race a pass when an attack waits for their reply, ending a Play phase without a card, otherwise a roll.
    /// </summary>
    private static (string Player, string Type, object? Payload) NextMove(JsonElement room)
    {
        var turn = TurnOf(room);
        var state = room.GetProperty("state");
        if (room.GetProperty("gameType").GetString() == "chog-life")
        {
            return state.TryGetProperty("choosing", out var choosing) && choosing.GetInt32() == SeatOf(room, turn)
                ? (turn, ChogLifeMatchRules.ChooseLane, new { lane = "Safe" })
                : (turn, ChogLifeMatchRules.Roll, null);
        }

        return state.GetProperty("phase").GetString() switch
        {
            "Reply" => (room.GetProperty("members").EnumerateArray()
                .Single(m => m.GetProperty("seat").GetInt32() == state.GetProperty("reply").GetProperty("target").GetInt32())
                .GetProperty("displayName").GetString()!, ChogRaceMatchRules.Pass, null),
            "Play" => (turn, ChogRaceMatchRules.EndTurn, null),
            _ => (turn, ChogRaceMatchRules.Roll, null),
        };
    }

    /// <summary>
    /// What every player must see alike: the pawns, how the match ended and each member's seat, rank and result. Chog
    /// Race's hands are private (DEC-056), so the rest of its state differs between players.
    /// </summary>
    private static string Shared(JsonElement room) => JsonSerializer.Serialize(new
    {
        status = room.GetProperty("status").GetString(),
        version = room.GetProperty("version").GetInt64(),
        pawns = room.GetProperty("state").GetProperty("pawns"),
        ended = room.GetProperty("state").GetProperty("ended"),
        members = room.GetProperty("members").EnumerateArray().Select(m => new
        {
            name = m.GetProperty("displayName").GetString(),
            seat = m.GetProperty("seat"),
            rank = m.TryGetProperty("rank", out var rank) ? rank.GetRawText() : null,
            result = m.TryGetProperty("result", out var result) ? result.GetRawText() : null,
        }),
    });

    private async Task<(int GamesPlayed, int Wins)[]> StatsAsync(params string[] names)
    {
        await using var db = database.CreateDbContext();
        var players = await db.Players.AsNoTracking().ToListAsync();
        return names.Select(name => players.Single(p => p.DisplayName == name)).Select(p => (p.GamesPlayed, p.Wins)).ToArray();
    }

    private async Task<string?> StoredStateAsync(string code)
    {
        await using var db = database.CreateDbContext();
        return await db.GameRooms.AsNoTracking().Where(r => r.Code == code).Select(r => r.State).SingleAsync();
    }

    [PostgresFact]
    public async Task A_chog_life_and_a_chog_race_match_play_to_the_finish_at_the_same_time_on_one_set_of_services()
    {
        var life = new[] { await GuestAsync("Alice"), await GuestAsync("Bob") };
        var race = new[] { await GuestAsync("Carol"), await GuestAsync("Dave") };
        var lifeCode = await StartedAsync("chog-life", life);
        var raceCode = await StartedAsync("chog-race", race);

        // Each player's live connection goes to the one room hub and follows their own room only.
        var listeners = new Dictionary<string, (Listener Listener, string Code)>();
        foreach (var (players, code) in new[] { (life, lifeCode), (race, raceCode) })
        {
            foreach (var player in players)
            {
                var listener = await ConnectAsync(player);
                Assert.Equal(code, (await listener.SubscribeAsync(code)).GetProperty("code").GetString());
                listeners[player.Name] = (listener, code);
            }
        }

        var lifeRoom = await ViewAsync(life[0], lifeCode);
        var raceRoom = await ViewAsync(race[0], raceCode);
        Assert.Equal(("chog-life", "chog-race"), (lifeRoom.GetProperty("gameType").GetString(), raceRoom.GetProperty("gameType").GetString()));
        Assert.True(lifeRoom.GetProperty("state").TryGetProperty("pawns", out _));
        Assert.Equal(RaceRulesV1Tiles, raceRoom.GetProperty("state").GetProperty("tiles").GetArrayLength());

        // Each step sends the next move of both matches at once, so the two games' commands meet in the shared services.
        var steps = 0;
        var bothActive = 0;
        while (lifeRoom.GetProperty("status").GetString() == "Active" || raceRoom.GetProperty("status").GetString() == "Active")
        {
            Assert.True(++steps < MaxSteps, $"A match is stuck after {steps} steps.");
            var moves = new List<Task<JsonElement>>();
            var active = new List<(Guest[] Players, JsonElement Room)>();
            foreach (var (players, room) in new[] { (life, lifeRoom), (race, raceRoom) })
            {
                if (room.GetProperty("status").GetString() != "Active")
                {
                    continue;
                }

                var (name, type, payload) = NextMove(room);
                moves.Add(CommandAsync(players.Single(p => p.Name == name), room, type, payload));
                active.Add((players, room));
            }

            bothActive += active.Count == 2 ? 1 : 0;
            await Task.WhenAll(moves);
            _clock.Advance(TimeSpan.FromSeconds(2));
            foreach (var (players, before) in active)
            {
                var after = await ViewAsync(players[0], before.GetProperty("code").GetString()!);
                Assert.True(after.GetProperty("version").GetInt64() > before.GetProperty("version").GetInt64(), $"Step {steps} changed nothing.");
                if (players == life)
                {
                    lifeRoom = after;
                }
                else
                {
                    raceRoom = after;
                }
            }
        }

        // Most of the race and the journey were played side by side.
        Assert.True(bothActive >= 10, $"Only {bothActive} steps had both matches running.");
        foreach (var (players, room) in new[] { (life, lifeRoom), (race, raceRoom) })
        {
            Assert.Equal("Completed", room.GetProperty("status").GetString());
            Assert.Equal("Finish", room.GetProperty("state").GetProperty("ended").GetString());
            Assert.Equal([1, 2], room.GetProperty("members").EnumerateArray().Select(m => m.GetProperty("rank").GetInt32()).Order());
            Assert.All(room.GetProperty("members").EnumerateArray(), m => Assert.Equal(JsonValueKind.Object, m.GetProperty("result").ValueKind));

            // Every player sees the same result, over REST and live, and their connection saw only their own room.
            var code = room.GetProperty("code").GetString()!;
            foreach (var player in players)
            {
                Assert.Equal(Shared(room), Shared(await ViewAsync(player, code)));
                var (listener, subscribed) = listeners[player.Name];
                Assert.Equal(code, subscribed);
                Assert.Equal(Shared(room), Shared(await listener.UntilAsync(code, room.GetProperty("version").GetInt64())));
            }
        }

        // One profile service counted both games: each player played once and each match had one winner.
        var stats = await StatsAsync("Alice", "Bob", "Carol", "Dave");
        Assert.All(stats, s => Assert.Equal(1, s.GamesPlayed));
        Assert.Equal(1, stats[0].Wins + stats[1].Wins);
        Assert.Equal(1, stats[2].Wins + stats[3].Wins);
    }

    /// <summary>Version 1's track length (DEC-054).</summary>
    private const int RaceRulesV1Tiles = 28;

    [PostgresFact]
    public async Task A_player_keeps_one_session_chog_and_profile_and_one_room_at_a_time_across_both_games()
    {
        var alice = await GuestAsync("Alice");
        var bob = await GuestAsync("Bob");
        await SendAsync(alice, HttpMethod.Put, "/api/characters/selected", new { kind = "Standard", id = "ocean" });

        var lifeCode = await StartedAsync("chog-life", alice, bob);

        // In a Chog Life match, the same room service keeps Alice and Bob out of any Chog Race room.
        var refused = await SendAsync(alice, HttpMethod.Post, "/api/rooms", new { gameType = "chog-race" }, HttpStatusCode.Conflict);
        Assert.Equal("already_in_room", refused.GetProperty("code").GetString());
        Assert.Equal(lifeCode, refused.GetProperty("roomCode").GetString());

        var lifeRoom = await ViewAsync(alice, lifeCode);
        Assert.Equal("ocean", Member(lifeRoom, "Alice").GetProperty("character").GetProperty("id").GetString());

        // Both games cap a match at 20 minutes; the one sweep ends it.
        _clock.Advance(TimeSpan.FromMinutes(21));
        Assert.Equal(1, await SweepAsync());
        Assert.Equal("Completed", (await ViewAsync(alice, lifeCode)).GetProperty("status").GetString());

        // The same session, Chog and profile then play Chog Race.
        var raceCode = await StartedAsync("chog-race", alice, bob);
        var raceRoom = await ViewAsync(alice, raceCode);
        Assert.Equal("Active", raceRoom.GetProperty("status").GetString());
        Assert.Equal(Member(lifeRoom, "Alice").GetProperty("avatarId").GetString(), Member(raceRoom, "Alice").GetProperty("avatarId").GetString());
        Assert.Equal("ocean", Member(raceRoom, "Alice").GetProperty("character").GetProperty("id").GetString());
        Assert.Equal(raceCode, (await SendAsync(alice, HttpMethod.Get, "/api/rooms/current")).GetProperty("code").GetString());

        _clock.Advance(TimeSpan.FromMinutes(21));
        Assert.Equal(1, await SweepAsync());
        Assert.Equal("Completed", (await ViewAsync(bob, raceCode)).GetProperty("status").GetString());

        foreach (var player in new[] { alice, bob })
        {
            var profile = await SendAsync(player, HttpMethod.Get, "/api/profile");
            Assert.Equal(2, profile.GetProperty("gamesPlayed").GetInt32());
        }

        // Neither pawn moved, so both share first place and a win in each game.
        foreach (var code in new[] { lifeCode, raceCode })
        {
            Assert.Equal([1, 1], (await ViewAsync(alice, code)).GetProperty("members").EnumerateArray().Select(m => m.GetProperty("rank").GetInt32()));
        }

        Assert.Equal([(2, 2), (2, 2)], await StatsAsync("Alice", "Bob"));
    }

    [PostgresFact]
    public async Task Commands_match_ids_and_subscriptions_meant_for_the_other_game_change_nothing()
    {
        var alice = await GuestAsync("Alice");
        var bob = await GuestAsync("Bob");
        var carol = await GuestAsync("Carol");
        var dave = await GuestAsync("Dave");
        var lifeCode = await StartedAsync("chog-life", alice, bob);
        var raceCode = await StartedAsync("chog-race", carol, dave);
        var lifeRoom = await ViewAsync(alice, lifeCode);
        var raceRoom = await ViewAsync(carol, raceCode);
        var lifeState = await StoredStateAsync(lifeCode);
        var raceState = await StoredStateAsync(raceCode);

        // A command of the other game is unknown to each game's rules.
        var unknown = await CommandAsync(carol, raceRoom, ChogLifeMatchRules.ChooseLane, new { lane = "Safe" }, HttpStatusCode.BadRequest);
        Assert.Equal("unknown_command", unknown.GetProperty("code").GetString());
        unknown = await CommandAsync(alice, lifeRoom, ChogRaceMatchRules.EndTurn, expected: HttpStatusCode.BadRequest);
        Assert.Equal("unknown_command", unknown.GetProperty("code").GetString());

        // The other match's id, or a room the player isn't in, looks like no room at all.
        await CommandAsync(carol, raceRoom, ChogRaceMatchRules.Roll, expected: HttpStatusCode.NotFound, matchId: lifeRoom.GetProperty("matchId").GetGuid());
        await CommandAsync(alice, lifeRoom, ChogLifeMatchRules.Roll, expected: HttpStatusCode.NotFound, matchId: raceRoom.GetProperty("matchId").GetGuid());
        await CommandAsync(alice, raceRoom, ChogRaceMatchRules.Roll, expected: HttpStatusCode.NotFound);
        await CommandAsync(carol, lifeRoom, ChogLifeMatchRules.Roll, expected: HttpStatusCode.NotFound, code: lifeCode);
        await SendAsync(carol, HttpMethod.Get, $"/api/rooms/{lifeCode}", expected: HttpStatusCode.NotFound);

        // The one room hub refuses a subscription to the other game's room.
        var live = await ConnectAsync(carol);
        var error = await Assert.ThrowsAsync<HubException>(() => live.SubscribeAsync(lifeCode));
        Assert.EndsWith(RoomHub.ErrorCodes.RoomNotFound, error.Message);

        Assert.Equal(lifeRoom.GetProperty("version").GetInt64(), (await ViewAsync(bob, lifeCode)).GetProperty("version").GetInt64());
        Assert.Equal(raceRoom.GetProperty("version").GetInt64(), (await ViewAsync(dave, raceCode)).GetProperty("version").GetInt64());
        Assert.Equal(lifeState, await StoredStateAsync(lifeCode));
        Assert.Equal(raceState, await StoredStateAsync(raceCode));
        Assert.Equal([(0, 0), (0, 0), (0, 0), (0, 0)], await StatsAsync("Alice", "Bob", "Carol", "Dave"));
    }

    [Fact]
    public async Task The_api_registers_one_room_session_match_and_realtime_service_for_both_games()
    {
        await using var factory = new ApiFactory(
            ApiFactory.UnusedDatabase, configureServices: services => _registrations.AddRange(services));
        _ = factory.Services;

        foreach (var shared in new[]
        {
            typeof(RoomService), typeof(LobbyService), typeof(MatchCommandService), typeof(TurnTimeoutService),
            typeof(RoomConnectionRegistry), typeof(RoomNotifier), typeof(IAuthenticationService),
        })
        {
            Assert.Single(_registrations, d => d.ServiceType == shared);
        }

        Assert.Equal(
            ["chog-life", "chog-race"],
            factory.Services.GetServices<IMatchRules>().Select(rules => rules.GameType).Order());
    }

    [Theory]
    [InlineData("chog-life")]
    [InlineData("chog-race")]
    public void A_game_module_adds_only_its_rules_and_ability_catalog(string game)
    {
        var services = new ServiceCollection();
        var module = game == "chog-life" ? typeof(ChogLifeMatchRules).Assembly : typeof(ChogRaceMatchRules).Assembly;
        _ = game == "chog-life" ? services.AddChogLife() : services.AddChogRace();

        Assert.NotEmpty(services);
        Assert.All(services, d => Assert.True(
            d.ServiceType == typeof(IMatchRules) || d.ServiceType == typeof(AbilityCatalog) || d.ServiceType.Assembly == module,
            $"{game} registers {d.ServiceType}, a service it doesn't own."));
    }
}
