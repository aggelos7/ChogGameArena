using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Channels;
using ChogGames.Api.ChogLife;
using ChogGames.Api.ChogRace;
using ChogGames.Api.ChogRoyale;
using ChogGames.Api.Matches;
using ChogGames.Api.Realtime;
using ChogGames.Core.Abilities;
using ChogGames.Core.Matches;
using ChogGames.Games.ChogLife;
using ChogGames.Games.ChogRace;
using ChogGames.Games.ChogRoyale;
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
/// STORY-057, DEC-061, STORY-084, DEC-094: one set of platform services serves all three games. A Chog Life, a Chog Race
/// and a two- or six-player Chog Royale match play to the finish at the same time, with commands for all of them sent
/// together, through the same room, match, session and realtime services and one room hub, and the Chog Royale match
/// ends within its five-minute target; a player keeps one session, Chog and profile across the games, and is in one
/// room at a time whichever game it plays. Commands, match ids and subscriptions meant for another game or room are
/// refused with nothing changed, and turning Chog Royale off leaves the other games playable. The game modules add only
/// their rules and ability catalogs, never a room, auth or realtime service of their own.
/// </summary>
public sealed class CrossGameTests(MigratedPostgresDatabase database) : IClassFixture<MigratedPostgresDatabase>, IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>More steps than the three matches take; reaching it means a turn is stuck.</summary>
    private const int MaxSteps = 600;

    private readonly ManualTimeProvider _clock = new(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
    private readonly List<IAsyncDisposable> _disposables = [];
    private readonly List<ServiceDescriptor> _registrations = [];
    private readonly SeededRandomSource _random = new(57);
    private ApiFactory _factory = null!;

    public async Task InitializeAsync()
    {
        if (database.ConnectionString.Length > 0)
        {
            await using var db = database.CreateDbContext();
            await db.Database.ExecuteSqlRawAsync("TRUNCATE \"GuestSessions\", \"Players\" CASCADE");
        }

        _factory = Factory(royale: "Available");
    }

    /// <summary>
    /// The API with Chog Life and Chog Race on, as with CHOG_LIFE_STATUS and CHOG_RACE_STATUS set to Available, and Chog
    /// Royale as <paramref name="royale"/> (CHOG_ROYALE_STATUS), on the test's database and clock.
    /// </summary>
    private ApiFactory Factory(string royale) => new(
        database.ConnectionString,
        new Dictionary<string, string?>
        {
            ["Games:Items:0:Status"] = "Available",
            ["Games:Items:1:Status"] = "Available",
            ["Games:Items:2:Status"] = royale,
            ["Matches:TurnTimeout"] = "00:05:00",

            // Every step reads every room, far more than a real player does in a minute.
            ["Rooms:RequestsPerMinutePerPlayer"] = "1000",
        },
        services =>
        {
            services.AddSingleton<IRandomSource>(_random);
            services.AddSingleton<TimeProvider>(_clock);
            foreach (var worker in services.Where(d => d.ImplementationType == typeof(TurnTimeoutWorker)).ToList())
            {
                services.Remove(worker);
            }
        });

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

    /// <summary>One pass of the turn timeout worker: overdue turns and rounds handled, and their rooms pushed.</summary>
    private async Task<int> SweepAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var timedOut = await scope.ServiceProvider.GetRequiredService<TurnTimeoutService>().TimeOutOverdueTurnsAsync(CancellationToken.None);
        var notifier = scope.ServiceProvider.GetRequiredService<RoomNotifier>();
        foreach (var turn in timedOut)
        {
            await notifier.RoomChangedAsync(turn.Code, turn.ForfeitedPlayerId);
        }

        return timedOut.Count;
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
    /// What every player must see alike: the board (Chog Life's and Chog Race's pawns, Chog Royale's balances and ledger),
    /// how the match ended and each member's seat, rank and result. Chog Race's and Chog Royale's hands are private
    /// (DEC-056, DEC-083), so the rest of their state differs between players.
    /// </summary>
    private static string Shared(JsonElement room)
    {
        var state = room.GetProperty("state");
        var royale = room.GetProperty("gameType").GetString() == "chog-royale";
        return JsonSerializer.Serialize(new
        {
            status = room.GetProperty("status").GetString(),
            version = room.GetProperty("version").GetInt64(),
            board = royale ? state.GetProperty("players") : state.GetProperty("pawns"),
            ended = royale ? state.GetProperty("ledger") : state.GetProperty("ended"),
            members = room.GetProperty("members").EnumerateArray().Select(m => new
            {
                name = m.GetProperty("displayName").GetString(),
                seat = m.GetProperty("seat"),
                rank = m.TryGetProperty("rank", out var rank) ? rank.GetRawText() : null,
                result = m.TryGetProperty("result", out var result) ? result.GetRawText() : null,
            }),
        });
    }

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

    private async Task<TimeSpan> PlayedForAsync(string code)
    {
        await using var db = database.CreateDbContext();
        var room = await db.GameRooms.AsNoTracking().SingleAsync(r => r.Code == code);
        return room.EndedAt!.Value - room.StartedAt!.Value;
    }

    /// <summary>
    /// Chog Royale's moves for this step (STORY-084): while a round takes actions, each player still playing who hasn't
    /// submitted plays the first card in their own hand, a Rug Pull or Whale Swap at the richest other player. Players on
    /// even seats lock it, so a round of only them resolves at once; the others leave it open until the deadline, which the
    /// sweep resolves.
    /// </summary>
    private static async Task<int> RoyaleMovesAsync(Guest[] players, JsonElement room)
    {
        var now = room.GetProperty("serverTime").GetDateTimeOffset();
        if (room.GetProperty("roundOpensAt").GetDateTimeOffset() > now || room.GetProperty("turnEndsAt").GetDateTimeOffset() <= now)
        {
            return 0;
        }

        var deciding = room.GetProperty("members").EnumerateArray()
            .Where(m => !m.GetProperty("hasLeft").GetBoolean() && !m.GetProperty("hasSubmitted").GetBoolean())
            .Select(m => players.Single(p => p.Name == m.GetProperty("displayName").GetString()))
            .ToArray();
        await Task.WhenAll(deciding.Select(async player =>
        {
            var view = await ViewAsync(player, room.GetProperty("code").GetString()!);
            var seat = SeatOf(view, player.Name);
            await CommandAsync(player, view, ChogRoyaleMatchRules.SubmitAction, RoyaleAction(view.GetProperty("state"), seat, locks: seat % 2 == 0));
        }));
        return deciding.Length;
    }

    private static object RoyaleAction(JsonElement state, int seat, bool locks)
    {
        if (state.GetProperty("hand").EnumerateArray().FirstOrDefault() is not { ValueKind: JsonValueKind.Object } card)
        {
            return new { hodl = true, @lock = locks };
        }

        int? target = card.GetProperty("card").GetString() is "RugPull" or "WhaleSwap"
            ? state.GetProperty("players").EnumerateArray()
                .Where(p => p.GetProperty("seat").GetInt32() != seat)
                .OrderByDescending(p => p.GetProperty("mon").GetInt32()).ThenBy(p => p.GetProperty("seat").GetInt32())
                .First().GetProperty("seat").GetInt32()
            : null;
        return new { card = card.GetProperty("id").GetString(), target, @lock = locks };
    }

    [PostgresTheory]
    [InlineData(2)]
    [InlineData(6)]
    public async Task All_three_games_play_to_the_finish_at_the_same_time_on_one_set_of_services(int royalePlayers)
    {
        var life = new[] { await GuestAsync("Alice"), await GuestAsync("Bob") };
        var race = new[] { await GuestAsync("Carol"), await GuestAsync("Dave") };
        var royale = new Guest[royalePlayers];
        for (var i = 0; i < royalePlayers; i++)
        {
            royale[i] = await GuestAsync($"Royal {i + 1}");
        }

        var lifeCode = await StartedAsync("chog-life", life);
        var raceCode = await StartedAsync("chog-race", race);
        var royaleCode = await StartedAsync("chog-royale", royale);

        // Each player's live connection goes to the one room hub and follows their own room only.
        var listeners = new Dictionary<string, (Listener Listener, string Code)>();
        foreach (var (players, code) in new[] { (life, lifeCode), (race, raceCode), (royale, royaleCode) })
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
        var royaleRoom = await ViewAsync(royale[0], royaleCode);
        Assert.Equal(
            ("chog-life", "chog-race", "chog-royale"),
            (lifeRoom.GetProperty("gameType").GetString(), raceRoom.GetProperty("gameType").GetString(), royaleRoom.GetProperty("gameType").GetString()));
        Assert.True(lifeRoom.GetProperty("state").TryGetProperty("pawns", out _));
        Assert.Equal(RaceRulesV1Tiles, raceRoom.GetProperty("state").GetProperty("tiles").GetArrayLength());
        Assert.Equal(("Rounds", royalePlayers), (royaleRoom.GetProperty("play").GetString(), royaleRoom.GetProperty("state").GetProperty("players").GetArrayLength()));

        // Each step sends the next move of every match at once, so the three games' commands meet in the shared services;
        // the sweep then runs as the worker would, resolving Chog Royale rounds whose deadline passed.
        var steps = 0;
        var allActive = 0;
        var royaleActions = 0;
        static bool Active(JsonElement room) => room.GetProperty("status").GetString() == "Active";
        while (Active(lifeRoom) || Active(raceRoom) || Active(royaleRoom))
        {
            Assert.True(++steps < MaxSteps, $"A match is stuck after {steps} steps.");
            var moves = new List<Task>();
            var active = new List<(Guest[] Players, JsonElement Room)>();
            foreach (var (players, room) in new[] { (life, lifeRoom), (race, raceRoom) })
            {
                if (!Active(room))
                {
                    continue;
                }

                var (name, type, payload) = NextMove(room);
                moves.Add(CommandAsync(players.Single(p => p.Name == name), room, type, payload));
                active.Add((players, room));
            }

            var royaleMoves = Active(royaleRoom) ? RoyaleMovesAsync(royale, royaleRoom) : Task.FromResult(0);
            moves.Add(royaleMoves);
            allActive += active.Count == 2 && Active(royaleRoom) ? 1 : 0;
            await Task.WhenAll(moves);
            royaleActions += await royaleMoves;
            _clock.Advance(TimeSpan.FromSeconds(2));
            await SweepAsync();
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

            royaleRoom = await ViewAsync(royale[0], royaleCode);
        }

        // Most of the Chog Royale match was played beside the other two, and every player acted in every round.
        Assert.True(allActive >= 10, $"Only {allActive} steps had all three matches running.");
        Assert.Equal(5 * royalePlayers, royaleActions);
        foreach (var (players, room) in new[] { (life, lifeRoom), (race, raceRoom) })
        {
            Assert.Equal("Completed", room.GetProperty("status").GetString());
            Assert.Equal("Finish", room.GetProperty("state").GetProperty("ended").GetString());
            Assert.Equal([1, 2], room.GetProperty("members").EnumerateArray().Select(m => m.GetProperty("rank").GetInt32()).Order());
        }

        // Chog Royale played all five rounds, none by default, within the five-minute target (ROYALE-003, ROYALE-065).
        Assert.Equal("Completed", royaleRoom.GetProperty("status").GetString());
        var ledger = royaleRoom.GetProperty("state").GetProperty("ledger");
        Assert.Equal(5, ledger.GetArrayLength());
        Assert.All(ledger.EnumerateArray().SelectMany(r => r.GetProperty("entries").EnumerateArray()), e => Assert.False(e.GetProperty("byDefault").GetBoolean()));
        Assert.Contains(royaleRoom.GetProperty("members").EnumerateArray(), m => m.GetProperty("rank").GetInt32() == 1);
        var played = await PlayedForAsync(royaleCode);
        Assert.True(played <= TimeSpan.FromMinutes(5), $"The {royalePlayers}-player Chog Royale match took {played}.");

        foreach (var (players, room) in new[] { (life, lifeRoom), (race, raceRoom), (royale, royaleRoom) })
        {
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

        // One profile service counted every game: each player played once and each match had its winners.
        var stats = await StatsAsync([.. life.Concat(race).Concat(royale).Select(p => p.Name)]);
        Assert.All(stats, s => Assert.Equal(1, s.GamesPlayed));
        Assert.Equal(1, stats[0].Wins + stats[1].Wins);
        Assert.Equal(1, stats[2].Wins + stats[3].Wins);
        Assert.Equal(
            royaleRoom.GetProperty("members").EnumerateArray().Count(m => m.GetProperty("rank").GetInt32() == 1),
            stats[4..].Sum(s => s.Wins));
    }

    /// <summary>Version 1's track length (DEC-054).</summary>
    private const int RaceRulesV1Tiles = 28;

    [PostgresFact]
    public async Task A_player_keeps_one_session_chog_and_profile_and_one_room_at_a_time_across_all_three_games()
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

        refused = await SendAsync(bob, HttpMethod.Post, "/api/rooms", new { gameType = "chog-royale" }, HttpStatusCode.Conflict);
        Assert.Equal(("already_in_room", raceCode), (refused.GetProperty("code").GetString(), refused.GetProperty("roomCode").GetString()));

        _clock.Advance(TimeSpan.FromMinutes(21));
        Assert.Equal(1, await SweepAsync());
        Assert.Equal("Completed", (await ViewAsync(bob, raceCode)).GetProperty("status").GetString());

        // And then Chog Royale, whose 5-minute cap the same sweep enforces.
        var royaleCode = await StartedAsync("chog-royale", alice, bob);
        var royaleRoom = await ViewAsync(alice, royaleCode);
        Assert.Equal("ocean", Member(royaleRoom, "Alice").GetProperty("character").GetProperty("id").GetString());
        Assert.Equal(royaleCode, (await SendAsync(bob, HttpMethod.Get, "/api/rooms/current")).GetProperty("code").GetString());
        refused = await SendAsync(alice, HttpMethod.Post, "/api/rooms", new { gameType = "chog-life" }, HttpStatusCode.Conflict);
        Assert.Equal(("already_in_room", royaleCode), (refused.GetProperty("code").GetString(), refused.GetProperty("roomCode").GetString()));

        _clock.Advance(TimeSpan.FromMinutes(6));
        Assert.Equal(1, await SweepAsync());
        Assert.Equal("Completed", (await ViewAsync(bob, royaleCode)).GetProperty("status").GetString());

        foreach (var player in new[] { alice, bob })
        {
            var profile = await SendAsync(player, HttpMethod.Get, "/api/profile");
            Assert.Equal(3, profile.GetProperty("gamesPlayed").GetInt32());
        }

        // Neither pawn moved and nobody's MON changed, so both share first place and a win in each game.
        foreach (var code in new[] { lifeCode, raceCode, royaleCode })
        {
            Assert.Equal([1, 1], (await ViewAsync(alice, code)).GetProperty("members").EnumerateArray().Select(m => m.GetProperty("rank").GetInt32()));
        }

        Assert.Equal([(3, 3), (3, 3)], await StatsAsync("Alice", "Bob"));
    }

    [PostgresFact]
    public async Task Commands_match_ids_and_subscriptions_meant_for_another_game_change_nothing()
    {
        var alice = await GuestAsync("Alice");
        var bob = await GuestAsync("Bob");
        var carol = await GuestAsync("Carol");
        var dave = await GuestAsync("Dave");
        var erin = await GuestAsync("Erin");
        var frank = await GuestAsync("Frank");
        var lifeCode = await StartedAsync("chog-life", alice, bob);
        var raceCode = await StartedAsync("chog-race", carol, dave);
        var royaleCode = await StartedAsync("chog-royale", erin, frank);

        // Chog Royale's first round takes actions after its 5 s announcement.
        _clock.Advance(TimeSpan.FromSeconds(5));
        var lifeRoom = await ViewAsync(alice, lifeCode);
        var raceRoom = await ViewAsync(carol, raceCode);
        var royaleRoom = await ViewAsync(erin, royaleCode);
        var lifeState = await StoredStateAsync(lifeCode);
        var raceState = await StoredStateAsync(raceCode);
        var royaleState = await StoredStateAsync(royaleCode);
        var hodl = new { hodl = true, @lock = true };

        // A command of another game is unknown to each game's rules.
        foreach (var (player, room, type, payload) in new (Guest, JsonElement, string, object?)[]
        {
            (carol, raceRoom, ChogLifeMatchRules.ChooseLane, new { lane = "Safe" }),
            (alice, lifeRoom, ChogRaceMatchRules.EndTurn, null),
            (alice, lifeRoom, ChogRoyaleMatchRules.SubmitAction, hodl),
            (carol, raceRoom, ChogRoyaleMatchRules.SubmitAction, hodl),
            (erin, royaleRoom, ChogLifeMatchRules.Roll, null),
            (erin, royaleRoom, ChogRaceMatchRules.PlayCard, new { card = "Pump" }),
        })
        {
            var unknown = await CommandAsync(player, room, type, payload, HttpStatusCode.BadRequest);
            Assert.Equal("unknown_command", unknown.GetProperty("code").GetString());
        }

        // Another match's id, or a room the player isn't in, looks like no room at all.
        await CommandAsync(carol, raceRoom, ChogRaceMatchRules.Roll, expected: HttpStatusCode.NotFound, matchId: lifeRoom.GetProperty("matchId").GetGuid());
        await CommandAsync(alice, lifeRoom, ChogLifeMatchRules.Roll, expected: HttpStatusCode.NotFound, matchId: raceRoom.GetProperty("matchId").GetGuid());
        await CommandAsync(erin, royaleRoom, ChogRoyaleMatchRules.SubmitAction, hodl, HttpStatusCode.NotFound, matchId: lifeRoom.GetProperty("matchId").GetGuid());
        await CommandAsync(alice, lifeRoom, ChogLifeMatchRules.Roll, expected: HttpStatusCode.NotFound, matchId: royaleRoom.GetProperty("matchId").GetGuid());
        await CommandAsync(alice, raceRoom, ChogRaceMatchRules.Roll, expected: HttpStatusCode.NotFound);
        await CommandAsync(carol, lifeRoom, ChogLifeMatchRules.Roll, expected: HttpStatusCode.NotFound, code: lifeCode);
        await CommandAsync(alice, royaleRoom, ChogRoyaleMatchRules.SubmitAction, hodl, HttpStatusCode.NotFound);
        await CommandAsync(erin, raceRoom, ChogRaceMatchRules.Roll, expected: HttpStatusCode.NotFound);
        await SendAsync(carol, HttpMethod.Get, $"/api/rooms/{lifeCode}", expected: HttpStatusCode.NotFound);
        await SendAsync(erin, HttpMethod.Get, $"/api/rooms/{lifeCode}", expected: HttpStatusCode.NotFound);
        await SendAsync(alice, HttpMethod.Get, $"/api/rooms/{royaleCode}", expected: HttpStatusCode.NotFound);

        // The one room hub refuses a subscription to another game's room.
        foreach (var (player, code) in new[] { (carol, lifeCode), (erin, raceCode), (alice, royaleCode) })
        {
            var live = await ConnectAsync(player);
            var error = await Assert.ThrowsAsync<HubException>(() => live.SubscribeAsync(code));
            Assert.EndsWith(RoomHub.ErrorCodes.RoomNotFound, error.Message);
        }

        Assert.Equal(lifeRoom.GetProperty("version").GetInt64(), (await ViewAsync(bob, lifeCode)).GetProperty("version").GetInt64());
        Assert.Equal(raceRoom.GetProperty("version").GetInt64(), (await ViewAsync(dave, raceCode)).GetProperty("version").GetInt64());
        Assert.Equal(royaleRoom.GetProperty("version").GetInt64(), (await ViewAsync(frank, royaleCode)).GetProperty("version").GetInt64());
        Assert.Equal(lifeState, await StoredStateAsync(lifeCode));
        Assert.Equal(raceState, await StoredStateAsync(raceCode));
        Assert.Equal(royaleState, await StoredStateAsync(royaleCode));
        Assert.False(Member(await ViewAsync(frank, royaleCode), "Erin").GetProperty("hasSubmitted").GetBoolean());
        Assert.Equal([(0, 0), (0, 0), (0, 0), (0, 0), (0, 0), (0, 0)], await StatsAsync("Alice", "Bob", "Carol", "Dave", "Erin", "Frank"));
    }

    [PostgresFact]
    public async Task Turning_chog_royale_off_refuses_new_royale_rooms_and_leaves_the_other_games_and_a_match_in_play_playable()
    {
        var (alice, bob, carol, dave, erin, frank) =
            (await GuestAsync("Alice"), await GuestAsync("Bob"), await GuestAsync("Carol"), await GuestAsync("Dave"), await GuestAsync("Erin"), await GuestAsync("Frank"));
        var royaleCode = await StartedAsync("chog-royale", alice, bob);

        // A redeploy with CHOG_ROYALE_STATUS=ComingSoon, on the same database: the session cookies still work.
        await _factory.DisposeAsync();
        _factory = Factory(royale: "ComingSoon");
        foreach (var guest in new[] { alice, bob, carol, dave, erin, frank })
        {
            guest.Client.Dispose();
        }

        (alice, bob, carol, dave, erin, frank) = (Again(alice), Again(bob), Again(carol), Again(dave), Again(erin), Again(frank));

        var games = await SendAsync(carol, HttpMethod.Get, "/api/games");
        Assert.Equal(
            [("chog-life", "Available"), ("chog-race", "Available"), ("chog-royale", "ComingSoon")],
            games.EnumerateArray().Select(g => (g.GetProperty("id").GetString(), g.GetProperty("status").GetString())).Order());
        var refused = await SendAsync(erin, HttpMethod.Post, "/api/rooms", new { gameType = "chog-royale" }, HttpStatusCode.Conflict);
        Assert.Equal("game_unavailable", refused.GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.NoContent, (await erin.Client.GetAsync("/api/rooms/current")).StatusCode);

        // The other games start and take their first move.
        var lifeCode = await StartedAsync("chog-life", carol, dave);
        var raceCode = await StartedAsync("chog-race", erin, frank);
        foreach (var (players, code) in new[] { (new[] { carol, dave }, lifeCode), (new[] { erin, frank }, raceCode) })
        {
            var room = await ViewAsync(players[0], code);
            var (name, type, payload) = NextMove(room);
            var moved = await CommandAsync(players.Single(p => p.Name == name), room, type, payload);
            Assert.True(moved.GetProperty("version").GetInt64() > room.GetProperty("version").GetInt64());
        }

        // The Chog Royale match already in play plays on to its end.
        _clock.Advance(TimeSpan.FromSeconds(5));
        for (var round = 1; round <= 5; round++)
        {
            foreach (var player in new[] { alice, bob })
            {
                await CommandAsync(player, await ViewAsync(player, royaleCode), ChogRoyaleMatchRules.SubmitAction, new { hodl = true, @lock = true });
            }

            _clock.Advance(TimeSpan.FromSeconds(20));
        }

        Assert.Equal("Completed", (await ViewAsync(alice, royaleCode)).GetProperty("status").GetString());
        Assert.Equal([(1, 1), (1, 1)], await StatsAsync("Alice", "Bob"));
    }

    /// <summary>The same browser against the current server, sending the session cookie it had.</summary>
    private Guest Again(Guest guest)
    {
        var client = _factory.CreateClient(
            new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), HandleCookies = false });
        _disposables.Add(new DisposableClient(client));
        client.DefaultRequestHeaders.Add("Cookie", guest.Cookie);
        return guest with { Client = client };
    }

    [Fact]
    public async Task The_api_registers_one_room_session_match_and_realtime_service_for_all_three_games()
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
            ["chog-life", "chog-race", "chog-royale"],
            factory.Services.GetServices<IMatchRules>().Select(rules => rules.GameType).Order());
    }

    [Theory]
    [InlineData("chog-life")]
    [InlineData("chog-race")]
    [InlineData("chog-royale")]
    public void A_game_module_adds_only_its_rules_and_ability_catalog(string game)
    {
        var services = new ServiceCollection();
        var module = game switch
        {
            "chog-life" => typeof(ChogLifeMatchRules).Assembly,
            "chog-race" => typeof(ChogRaceMatchRules).Assembly,
            _ => typeof(ChogRoyaleMatchRules).Assembly,
        };
        _ = game switch
        {
            "chog-life" => services.AddChogLife(),
            "chog-race" => services.AddChogRace(),
            _ => services.AddChogRoyale(),
        };

        Assert.NotEmpty(services);
        Assert.All(services, d => Assert.True(
            d.ServiceType == typeof(IMatchRules) || d.ServiceType == typeof(AbilityCatalog) || d.ServiceType.Assembly == module,
            $"{game} registers {d.ServiceType}, a service it doesn't own."));
    }
}
