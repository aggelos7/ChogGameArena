using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Channels;
using ChogGames.Api.Matches;
using ChogGames.Api.Realtime;
using ChogGames.Core.Matches;
using ChogGames.Infrastructure.Matches;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ChogGames.Api.Tests.ChogLife;

/// <summary>
/// STORY-037/038, DEC-039/040: on their turn a player rolls; the server draws the die, moves the pawn, resolves the tile
/// it stops on and ends the turn, and every player, over REST and live, gets the same roll, destination and MON. Rolls
/// by anyone else change nothing and draw nothing. STORY-039, DEC-041: a roll onto a fork keeps the turn until the
/// player chooses a lane; only the offered lanes are accepted, and a turn that runs out first takes the safe lane.
/// </summary>
public sealed class ChogLifeMovementTests(MigratedPostgresDatabase database) : IClassFixture<MigratedPostgresDatabase>, IAsyncLifetime
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

    /// <summary>A browser with a guest session; the cookie is kept so live connections can send it too.</summary>
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

    /// <summary>One live connection and the snapshots it receives.</summary>
    private sealed class Listener(HubConnection connection) : IAsyncDisposable
    {
        private readonly Channel<JsonElement> _snapshots = Channel.CreateUnbounded<JsonElement>();

        public void Listen() => connection.On<JsonElement>(nameof(IRoomClient.RoomUpdated), room => _snapshots.Writer.TryWrite(room));

        public Task StartAsync() => connection.StartAsync();

        public Task<JsonElement> SubscribeAsync(string code) => connection.InvokeAsync<JsonElement>(nameof(RoomHub.SubscribeRoom), code);

        public async Task<JsonElement> NextAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            return await _snapshots.Reader.ReadAsync(timeout.Token);
        }

        public ValueTask DisposeAsync() => connection.DisposeAsync();
    }

    private async Task<Listener> ConnectAsync(Guest guest, string code)
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
        await listener.SubscribeAsync(code);
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

    /// <summary>A started Chog Life match of <paramref name="players"/>, seated in join order; returns its code.</summary>
    private static async Task<string> StartedAsync(params Guest[] players)
    {
        var created = await SendAsync(players[0], HttpMethod.Post, "/api/rooms", new { gameType = "chog-life" }, HttpStatusCode.Created);
        var code = created.GetProperty("code").GetString()!;
        foreach (var player in players[1..])
        {
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

    /// <summary>Alice rolls 6 (a market, +10), Bob 1 and Alice 5, which stops her on the first fork (tile 9).</summary>
    private async Task<JsonElement> AtForkAsync(Guest alice, Guest bob, string code)
    {
        _dice.Enqueue(6, 0, 1, 5);
        await RollAsync(alice, await ViewAsync(alice, code));
        await RollAsync(bob, await ViewAsync(bob, code));
        await RollAsync(alice, await ViewAsync(alice, code));
        return await ViewAsync(alice, code);
    }

    private static int? Choosing(JsonElement room) =>
        room.GetProperty("state").TryGetProperty("choosing", out var seat) ? seat.GetInt32() : null;

    private static string LastMove(JsonElement room) => room.GetProperty("state").GetProperty("lastMove").GetRawText();

    private static string Pawns(JsonElement room) => room.GetProperty("state").GetProperty("pawns").GetRawText();

    private static int? TurnSeat(JsonElement room) =>
        room.GetProperty("members").EnumerateArray().Single(m => m.GetProperty("isTurn").GetBoolean()).GetProperty("seat").GetInt32();

    private async Task<string?> StoredStateAsync(string code)
    {
        await using var db = database.CreateDbContext();
        var state = await db.GameRooms.AsNoTracking().Where(r => r.Code == code).Select(r => r.State).SingleAsync();
        return state is null ? null : JsonSerializer.Serialize(JsonDocument.Parse(state).RootElement);
    }

    [PostgresFact]
    public async Task Every_player_gets_the_same_roll_and_destination_live_and_over_rest()
    {
        var alice = await GuestAsync("Alice");
        var bob = await GuestAsync("Bob");
        var code = await StartedAsync(alice, bob);
        var aliceLive = await ConnectAsync(alice, code);
        var bobLive = await ConnectAsync(bob, code);
        var room = await ViewAsync(alice, code);
        var drawsBefore = _dice.Draws.Count;
        _dice.Enqueue(4);

        var result = await RollAsync(alice, room);

        Assert.Equal(room.GetProperty("version").GetInt64() + 1, result.GetProperty("version").GetInt64());
        Assert.Equal([(1, 7)], _dice.Draws.Skip(drawsBefore));
        const string move = """{"seat":1,"roll":4,"from":0,"to":4,"path":[1,2,3,4]}""";
        const string pawns = """[{"seat":1,"tile":4,"mon":100},{"seat":2,"tile":0,"mon":100}]""";
        foreach (var seen in new[] { await aliceLive.NextAsync(), await bobLive.NextAsync(), await ViewAsync(alice, code), await ViewAsync(bob, code) })
        {
            Assert.Equal(result.GetProperty("version").GetInt64(), seen.GetProperty("version").GetInt64());
            Assert.Equal((move, pawns), (LastMove(seen), Pawns(seen)));
            Assert.Equal(2, TurnSeat(seen));
        }
    }

    [PostgresFact]
    public async Task Players_take_turns_rolling_and_a_move_stops_on_the_branch_for_the_lane_choice()
    {
        var alice = await GuestAsync("Alice");
        var bob = await GuestAsync("Bob");
        var code = await StartedAsync(alice, bob);

        await AtForkAsync(alice, bob, code);

        var room = await ViewAsync(bob, code);
        Assert.Equal("""[{"seat":1,"tile":9,"mon":110},{"seat":2,"tile":1,"mon":100}]""", Pawns(room));
        Assert.Equal("""{"seat":1,"roll":5,"from":6,"to":9,"path":[7,8,9]}""", LastMove(room));
        Assert.Equal(1, TurnSeat(room));
        Assert.Equal(1, Choosing(room));
        Assert.Equal("Safe", room.GetProperty("state").GetProperty("defaultLane").GetString());
    }

    [PostgresFact]
    public async Task The_chosen_lane_passes_the_turn_reaches_everyone_and_the_next_roll_takes_it()
    {
        var alice = await GuestAsync("Alice");
        var bob = await GuestAsync("Bob");
        var code = await StartedAsync(alice, bob);
        var room = await AtForkAsync(alice, bob, code);
        var aliceLive = await ConnectAsync(alice, code);
        var bobLive = await ConnectAsync(bob, code);
        var drawsBefore = _dice.Draws.Count;

        var result = await ChooseAsync(alice, room, new { lane = "Degen" });

        Assert.Equal(drawsBefore, _dice.Draws.Count);
        const string move = """{"seat":1,"roll":5,"from":6,"to":9,"path":[7,8,9],"lane":"Degen"}""";
        const string pawns = """[{"seat":1,"tile":9,"mon":110,"lane":"Degen"},{"seat":2,"tile":1,"mon":100}]""";
        foreach (var seen in new[] { await aliceLive.NextAsync(), await bobLive.NextAsync(), await ViewAsync(alice, code), await ViewAsync(bob, code) })
        {
            Assert.Equal(result.GetProperty("version").GetInt64(), seen.GetProperty("version").GetInt64());
            Assert.Equal((move, pawns), (LastMove(seen), Pawns(seen)));
            Assert.Null(Choosing(seen));
            Assert.Equal(2, TurnSeat(seen));
        }

        // Bob rolls 3 onto a normal tile; Alice's 1 takes her onto the degen lane's rug pull, which takes 40% of 110.
        _dice.Enqueue(3, 1);
        await RollAsync(bob, await ViewAsync(bob, code));
        await RollAsync(alice, await ViewAsync(alice, code));

        var after = await ViewAsync(bob, code);
        Assert.Equal("""{"seat":1,"roll":1,"from":9,"to":14,"path":[14],"change":-44}""", LastMove(after));
        Assert.Equal("""[{"seat":1,"tile":14,"mon":66},{"seat":2,"tile":4,"mon":100}]""", Pawns(after));
    }

    [PostgresFact]
    public async Task Only_offered_lanes_are_accepted_and_refusals_change_nothing()
    {
        var alice = await GuestAsync("Alice");
        var bob = await GuestAsync("Bob");
        var mallory = await GuestAsync("Mallory");
        var code = await StartedAsync(alice, bob);
        var room = await AtForkAsync(alice, bob, code);
        var before = await StoredStateAsync(code);
        var drawsBefore = _dice.Draws.Count;

        foreach (var payload in new object?[] { new { lane = "Main" }, new { lane = "degen" }, new { lane = 1 }, new { }, null })
        {
            var refused = await ChooseAsync(alice, room, payload, HttpStatusCode.Conflict);
            Assert.Equal(("command_rejected", "invalid_lane"), (refused.GetProperty("code").GetString(), refused.GetProperty("reason").GetString()));
        }

        var rolled = await RollAsync(alice, room, HttpStatusCode.Conflict);
        Assert.Equal("choose_lane_first", rolled.GetProperty("reason").GetString());
        var outOfTurn = await ChooseAsync(bob, room, new { lane = "Degen" }, HttpStatusCode.Conflict);
        Assert.Equal("not_your_turn", outOfTurn.GetProperty("code").GetString());
        var outsider = await ChooseAsync(mallory, room, new { lane = "Degen" }, HttpStatusCode.NotFound);
        Assert.Equal("room_not_found", outsider.GetProperty("code").GetString());

        Assert.Equal(before, await StoredStateAsync(code));
        Assert.Equal(room.GetProperty("version").GetInt64(), (await ViewAsync(alice, code)).GetProperty("version").GetInt64());
        Assert.Equal(drawsBefore, _dice.Draws.Count);

        // Once chosen, there's nothing left to choose; Bob has none pending on his own turn either.
        await ChooseAsync(alice, room, new { lane = "Safe" });
        var noneLeft = await ChooseAsync(bob, await ViewAsync(bob, code), new { lane = "Degen" }, HttpStatusCode.Conflict);
        Assert.Equal("no_choice_pending", noneLeft.GetProperty("reason").GetString());
    }

    [PostgresFact]
    public async Task A_turn_that_runs_out_while_choosing_takes_the_safe_lane_and_counts_a_miss()
    {
        var alice = await GuestAsync("Alice");
        var bob = await GuestAsync("Bob");
        var code = await StartedAsync(alice, bob);
        await AtForkAsync(alice, bob, code);

        _clock.Advance(TimeSpan.FromMinutes(6));
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var timedOut = await scope.ServiceProvider.GetRequiredService<TurnTimeoutService>().TimeOutOverdueTurnsAsync(CancellationToken.None);
            Assert.Single(timedOut);
        }

        var room = await ViewAsync(bob, code);
        Assert.Equal("""{"seat":1,"roll":5,"from":6,"to":9,"path":[7,8,9],"lane":"Safe","byDefault":true}""", LastMove(room));
        Assert.Equal("""[{"seat":1,"tile":9,"mon":110,"lane":"Safe"},{"seat":2,"tile":1,"mon":100}]""", Pawns(room));
        Assert.Null(Choosing(room));
        Assert.Equal(2, TurnSeat(room));
        await using (var db = database.CreateDbContext())
        {
            var stored = await db.GameRooms.AsNoTracking().SingleAsync(r => r.Code == code);
            Assert.Equal(1, stored.Members.Single(m => m.Seat == 1).MissedTurns);
        }

        _dice.Enqueue(1, 1);
        await RollAsync(bob, room);
        await RollAsync(alice, await ViewAsync(alice, code));
        Assert.Equal("""{"seat":1,"roll":1,"from":9,"to":10,"path":[10],"change":10}""", LastMove(await ViewAsync(bob, code)));
    }

    [PostgresFact]
    public async Task Rolls_out_of_turn_or_from_outside_change_nothing_and_draw_nothing()
    {
        var alice = await GuestAsync("Alice");
        var bob = await GuestAsync("Bob");
        var mallory = await GuestAsync("Mallory");
        var code = await StartedAsync(alice, bob);
        var room = await ViewAsync(bob, code);
        var before = await StoredStateAsync(code);
        var drawsBefore = _dice.Draws.Count;

        var outOfTurn = await RollAsync(bob, room, HttpStatusCode.Conflict);
        Assert.Equal("not_your_turn", outOfTurn.GetProperty("code").GetString());
        var outsider = await RollAsync(mallory, room, HttpStatusCode.NotFound);
        Assert.Equal("room_not_found", outsider.GetProperty("code").GetString());

        Assert.Equal(before, await StoredStateAsync(code));
        Assert.Equal(room.GetProperty("version").GetInt64(), (await ViewAsync(alice, code)).GetProperty("version").GetInt64());
        Assert.Equal(drawsBefore, _dice.Draws.Count);
    }

    [PostgresFact]
    public async Task A_forged_roll_is_ignored_and_a_retry_does_not_roll_again()
    {
        var alice = await GuestAsync("Alice");
        var bob = await GuestAsync("Bob");
        var code = await StartedAsync(alice, bob);
        var room = await ViewAsync(alice, code);
        var actionId = Guid.NewGuid();
        _dice.Enqueue(2);

        await RollAsync(alice, room, payload: new { roll = 6, to = 35 }, actionId: actionId);
        var drawsAfter = _dice.Draws.Count;
        var retried = await RollAsync(alice, room, payload: new { roll = 6, to = 35 }, actionId: actionId);

        Assert.True(retried.GetProperty("replayed").GetBoolean());
        Assert.Equal(drawsAfter, _dice.Draws.Count);
        var seen = await ViewAsync(bob, code);
        Assert.Equal("""{"seat":1,"roll":2,"from":0,"to":2,"path":[1,2],"change":20}""", LastMove(seen));
        Assert.Equal("""[{"seat":1,"tile":2,"mon":120},{"seat":2,"tile":0,"mon":100}]""", Pawns(seen));
    }

    [PostgresFact]
    public async Task Every_player_sees_the_same_event_outcome_and_mon_live_and_over_rest()
    {
        var alice = await GuestAsync("Alice");
        var bob = await GuestAsync("Bob");
        var code = await StartedAsync(alice, bob);
        var aliceLive = await ConnectAsync(alice, code);
        var bobLive = await ConnectAsync(bob, code);
        var room = await ViewAsync(alice, code);
        var drawsBefore = _dice.Draws.Count;
        _dice.Enqueue(3, 95);

        var result = await RollAsync(alice, room);

        Assert.Equal([(1, 7), (0, 100)], _dice.Draws.Skip(drawsBefore));
        const string move = """{"seat":1,"roll":3,"from":0,"to":3,"path":[1,2,3],"change":50}""";
        const string pawns = """[{"seat":1,"tile":3,"mon":150},{"seat":2,"tile":0,"mon":100}]""";
        foreach (var seen in new[] { await aliceLive.NextAsync(), await bobLive.NextAsync(), await ViewAsync(alice, code), await ViewAsync(bob, code) })
        {
            Assert.Equal(result.GetProperty("version").GetInt64(), seen.GetProperty("version").GetInt64());
            Assert.Equal((move, pawns), (LastMove(seen), Pawns(seen)));
        }
    }
}
