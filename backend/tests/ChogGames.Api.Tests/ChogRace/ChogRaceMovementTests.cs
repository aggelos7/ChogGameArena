using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Channels;
using ChogGames.Api.Matches;
using ChogGames.Api.Realtime;
using ChogGames.Core.Matches;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ChogGames.Api.Tests.ChogRace;

/// <summary>
/// STORY-051, DEC-055: on their turn a player rolls; the server rolls 1–3, moves the pawn, resolves the tile it lands on
/// and ends the turn, and every player, over REST and live, gets the same move. Commands out of phase, out of turn or from
/// outside change nothing and draw nothing. The first pawn to reach the Finish wins and the match ends with ranks.
/// STORY-052, DEC-056: a Card tile draws from the server's deck, and only the player who drew sees the card. STORY-053,
/// DEC-057: holding a movement or chaos card opens the Play phase, where <c>PLAY_CARD</c> plays it and ends the turn.
/// </summary>
public sealed class ChogRaceMovementTests(MigratedPostgresDatabase database) : IClassFixture<MigratedPostgresDatabase>, IAsyncLifetime
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

    /// <summary>
    /// A started Chog Race match of <paramref name="players"/>, seated in join order; returns its code. The clock moves on
    /// between joins, since players joining at the same instant are seated by id.
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

    private static Task<JsonElement> EndTurnAsync(Guest guest, JsonElement room, HttpStatusCode expected = HttpStatusCode.OK) =>
        CommandAsync(guest, room, "END_TURN", null, expected);

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

    private static string LastMove(JsonElement room) => room.GetProperty("state").GetProperty("lastMove").GetRawText();

    private static string Pawns(JsonElement room) => room.GetProperty("state").GetProperty("pawns").GetRawText();

    private static string? Phase(JsonElement room) =>
        room.GetProperty("state").TryGetProperty("phase", out var phase) ? phase.GetString() : null;

    private static int? TurnSeat(JsonElement room) =>
        room.GetProperty("members").EnumerateArray().SingleOrDefault(m => m.GetProperty("isTurn").GetBoolean()) is { ValueKind: JsonValueKind.Object } member
            ? member.GetProperty("seat").GetInt32()
            : null;

    private async Task<(long Version, string? State)> StoredAsync(string code)
    {
        await using var db = database.CreateDbContext();
        var stored = await db.GameRooms.AsNoTracking().Where(r => r.Code == code).Select(r => new { r.Version, r.State }).SingleAsync();
        return (stored.Version, stored.State is null ? null : JsonSerializer.Serialize(JsonDocument.Parse(stored.State).RootElement));
    }

    [PostgresFact]
    public async Task Every_player_gets_the_same_move_live_and_over_rest()
    {
        var alice = await GuestAsync("Alice");
        var bob = await GuestAsync("Bob");
        var code = await StartedAsync(alice, bob);
        var aliceLive = await ConnectAsync(alice, code);
        var bobLive = await ConnectAsync(bob, code);
        var room = await ViewAsync(alice, code);
        var drawsBefore = _dice.Draws.Count;
        _dice.Enqueue(2);

        var result = await RollAsync(alice, room);

        Assert.Equal(room.GetProperty("version").GetInt64() + 1, result.GetProperty("version").GetInt64());
        Assert.Equal([(1, 4)], _dice.Draws.Skip(drawsBefore));
        var alices = new[] { await aliceLive.NextAsync(), await ViewAsync(alice, code) };
        var bobs = new[] { await bobLive.NextAsync(), await ViewAsync(bob, code) };
        Assert.Single(alices.Select(s => s.GetProperty("state").GetRawText()).Distinct());
        Assert.Single(bobs.Select(s => s.GetProperty("state").GetRawText()).Distinct());
        foreach (var snapshot in alices.Concat(bobs))
        {
            Assert.Equal(result.GetProperty("version").GetInt64(), snapshot.GetProperty("version").GetInt64());
            Assert.Equal("""[{"seat":1,"tile":2,"cards":1},{"seat":2,"tile":0,"cards":0}]""", Pawns(snapshot));
            Assert.Equal((23, 0), (snapshot.GetProperty("state").GetProperty("deck").GetInt32(), snapshot.GetProperty("state").GetProperty("discard").GetInt32()));
            // Pump can be played on the turn, so Alice's turn moves on to its Play phase.
            Assert.Equal((1, "Play"), (TurnSeat(snapshot), Phase(snapshot)));
        }

        // The deck was shuffled at the start without reordering it, so its top card is the first Pump.
        Assert.Equal("""{"seat":1,"roll":2,"from":0,"landed":2,"to":2,"effect":"Card","draw":"Drawn","card":"Pump"}""", LastMove(alices[0]));
        Assert.Equal("""["Pump"]""", alices[0].GetProperty("state").GetProperty("hand").GetRawText());
        Assert.Equal("""{"seat":1,"roll":2,"from":0,"landed":2,"to":2,"effect":"Card","draw":"Drawn"}""", LastMove(bobs[0]));
        Assert.Equal("[]", bobs[0].GetProperty("state").GetProperty("hand").GetRawText());
        Assert.DoesNotContain("Pump", bobs[0].GetRawText());
    }

    [PostgresFact]
    public async Task A_full_hand_draws_nothing_and_stays_hidden_from_the_other_players()
    {
        var alice = await GuestAsync("Alice");
        var bob = await GuestAsync("Bob");
        var code = await StartedAsync(alice, bob);
        await using (var db = database.CreateDbContext())
        {
            await db.Database.ExecuteSqlRawAsync(
                """UPDATE "GameRooms" SET "State" = {0}::jsonb WHERE "Code" = {1}""",
                """{"rulesVersion":1,"pawns":[{"seat":1,"tile":0,"hand":["Rug","Hodl","Liquidated"]},{"seat":2,"tile":0}],"deck":["Fomo"],"discard":[]}""",
                code);
        }

        _dice.Enqueue(2);
        await RollAsync(alice, await ViewAsync(alice, code));

        var mine = await ViewAsync(alice, code);
        var theirs = await ViewAsync(bob, code);
        Assert.Equal("""["Rug","Hodl","Liquidated"]""", mine.GetProperty("state").GetProperty("hand").GetRawText());
        Assert.Equal("""{"seat":1,"roll":2,"from":0,"landed":2,"to":2,"effect":"Card","draw":"HandFull"}""", LastMove(theirs));
        Assert.Equal("""[{"seat":1,"tile":2,"cards":3},{"seat":2,"tile":0,"cards":0}]""", Pawns(theirs));
        Assert.Equal(1, theirs.GetProperty("state").GetProperty("deck").GetInt32());
        foreach (var card in new[] { "Rug", "Hodl", "Liquidated", "Fomo" })
        {
            Assert.DoesNotContain(card, theirs.GetRawText());
        }

        Assert.Equal(2, TurnSeat(theirs));
    }

    [PostgresFact]
    public async Task Players_take_turns_and_boosts_and_setbacks_send_pawns_on()
    {
        var alice = await GuestAsync("Alice");
        var bob = await GuestAsync("Bob");
        var code = await StartedAsync(alice, bob);

        // Alice 3 (tile 3), Bob 1 (tile 1), Alice 1 onto the Boost at 4 (to 6), Bob 3 onto the Boost at 4, Alice 3 onto the
        // Setback at 9, back to the Card tile at 7, which draws her the top card, a Pump she can play.
        _dice.Enqueue(3, 1, 1, 3, 3);
        foreach (var player in new[] { alice, bob, alice })
        {
            await RollAsync(player, await ViewAsync(player, code));
        }

        Assert.Equal("""{"seat":1,"roll":1,"from":3,"landed":4,"to":6,"effect":"Boost"}""", LastMove(await ViewAsync(bob, code)));
        await RollAsync(bob, await ViewAsync(bob, code));
        await RollAsync(alice, await ViewAsync(alice, code));

        var room = await ViewAsync(bob, code);
        Assert.Equal("""{"seat":1,"roll":3,"from":6,"landed":9,"to":7,"effect":"Setback","draw":"Drawn"}""", LastMove(room));
        Assert.Equal("""[{"seat":1,"tile":7,"cards":1},{"seat":2,"tile":6,"cards":0}]""", Pawns(room));
        Assert.Equal((1, "Play"), (TurnSeat(room), Phase(room)));
    }

    [PostgresFact]
    public async Task Commands_out_of_phase_out_of_turn_or_from_outside_change_nothing_and_draw_nothing()
    {
        var alice = await GuestAsync("Alice");
        var bob = await GuestAsync("Bob");
        var mallory = await GuestAsync("Mallory");
        var code = await StartedAsync(alice, bob);
        var room = await ViewAsync(bob, code);
        var before = await StoredAsync(code);
        var drawsBefore = _dice.Draws.Count;

        var early = await EndTurnAsync(alice, room, HttpStatusCode.Conflict);
        Assert.Equal(("command_rejected", "must_roll_first"), (early.GetProperty("code").GetString(), early.GetProperty("reason").GetString()));
        var outOfTurn = await RollAsync(bob, room, HttpStatusCode.Conflict);
        Assert.Equal("not_your_turn", outOfTurn.GetProperty("code").GetString());
        var outsider = await RollAsync(mallory, room, HttpStatusCode.NotFound);
        Assert.Equal("room_not_found", outsider.GetProperty("code").GetString());
        var card = await CommandAsync(alice, room, "PLAY_CARD", new { card = "Pump" }, HttpStatusCode.Conflict);
        Assert.Equal(("command_rejected", "must_roll_first"), (card.GetProperty("code").GetString(), card.GetProperty("reason").GetString()));

        Assert.Equal(before, await StoredAsync(code));
        Assert.Equal(drawsBefore, _dice.Draws.Count);

        // Once rolled, the turn has passed: the old view is stale and Alice's End turn is out of turn.
        _dice.Enqueue(1);
        await RollAsync(alice, room);
        var stale = await RollAsync(alice, room, HttpStatusCode.Conflict);
        Assert.Equal("stale_version", stale.GetProperty("code").GetString());
        var ended = await EndTurnAsync(alice, await ViewAsync(alice, code), HttpStatusCode.Conflict);
        Assert.Equal("not_your_turn", ended.GetProperty("code").GetString());
    }

    [PostgresFact]
    public async Task A_forged_roll_is_ignored_and_a_retry_does_not_roll_again()
    {
        var alice = await GuestAsync("Alice");
        var bob = await GuestAsync("Bob");
        var code = await StartedAsync(alice, bob);
        var room = await ViewAsync(alice, code);
        var actionId = Guid.NewGuid();
        _dice.Enqueue(1);

        await RollAsync(alice, room, payload: new { roll = 3, to = 27 }, actionId: actionId);
        var drawsAfter = _dice.Draws.Count;
        var retried = await RollAsync(alice, room, payload: new { roll = 3, to = 27 }, actionId: actionId);

        Assert.True(retried.GetProperty("replayed").GetBoolean());
        Assert.Equal(drawsAfter, _dice.Draws.Count);
        Assert.Equal("""{"seat":1,"roll":1,"from":0,"landed":1,"to":1}""", LastMove(await ViewAsync(bob, code)));
    }

    [PostgresFact]
    public async Task The_first_pawn_to_reach_the_finish_wins_and_the_match_ends_for_everyone()
    {
        var alice = await GuestAsync("Alice");
        var bob = await GuestAsync("Bob");
        var carol = await GuestAsync("Carol");
        var code = await StartedAsync(alice, bob, carol);
        await using (var db = database.CreateDbContext())
        {
            // Alice one roll from the finish; Bob and Carol behind her.
            await db.Database.ExecuteSqlRawAsync(
                """UPDATE "GameRooms" SET "State" = {0}::jsonb WHERE "Code" = {1}""",
                """{"rulesVersion":1,"pawns":[{"seat":1,"tile":25},{"seat":2,"tile":10},{"seat":3,"tile":15}]}""",
                code);
        }

        var bobLive = await ConnectAsync(bob, code);
        _dice.Enqueue(3);
        var room = await ViewAsync(alice, code);
        await RollAsync(alice, room);

        foreach (var seen in new[] { await bobLive.NextAsync(), await ViewAsync(alice, code), await ViewAsync(carol, code) })
        {
            Assert.Equal("Completed", seen.GetProperty("status").GetString());
            Assert.Equal("""{"seat":1,"roll":3,"from":25,"landed":27,"to":27,"effect":"Finish"}""", LastMove(seen));
            Assert.Equal(1, seen.GetProperty("state").GetProperty("winner").GetInt32());
            Assert.Null(Phase(seen));
            Assert.Equal(
                [("Alice", 1), ("Bob", 3), ("Carol", 2)],
                seen.GetProperty("members").EnumerateArray().Select(m => (m.GetProperty("displayName").GetString(), m.GetProperty("rank").GetInt32())));
        }

        var after = await RollAsync(bob, await ViewAsync(bob, code), HttpStatusCode.Conflict);
        Assert.Equal("room_closed", after.GetProperty("code").GetString());
    }

    private static Task<JsonElement> PlayCardAsync(
        Guest guest, JsonElement room, object payload, HttpStatusCode expected = HttpStatusCode.OK, Guid? actionId = null) =>
        CommandAsync(guest, room, "PLAY_CARD", payload, expected, actionId);

    [PostgresFact]
    public async Task A_card_played_moves_the_pawn_for_everyone_and_a_retry_does_not_play_it_again()
    {
        var alice = await GuestAsync("Alice");
        var bob = await GuestAsync("Bob");
        var code = await StartedAsync(alice, bob);
        await using (var db = database.CreateDbContext())
        {
            await db.Database.ExecuteSqlRawAsync(
                """UPDATE "GameRooms" SET "State" = {0}::jsonb WHERE "Code" = {1}""",
                """{"rulesVersion":1,"pawns":[{"seat":1,"tile":0,"hand":["Pump","Rug"]},{"seat":2,"tile":0}],"deck":["Hodl"],"discard":[]}""",
                code);
        }

        _dice.Enqueue(3);
        await RollAsync(alice, await ViewAsync(alice, code));
        var room = await ViewAsync(alice, code);
        Assert.Equal((1, "Play"), (TurnSeat(room), Phase(room)));
        Assert.Equal("""["Pump"]""", room.GetProperty("state").GetProperty("playable").GetRawText());
        var bobLive = await ConnectAsync(bob, code);
        var actionId = Guid.NewGuid();
        var drawsBefore = _dice.Draws.Count;

        var played = await PlayCardAsync(alice, room, new { card = "Pump" }, actionId: actionId);
        var retried = await PlayCardAsync(alice, room, new { card = "Pump" }, actionId: actionId);

        Assert.True(retried.GetProperty("replayed").GetBoolean());
        Assert.Equal(played.GetProperty("version").GetInt64(), retried.GetProperty("version").GetInt64());
        Assert.Equal(drawsBefore, _dice.Draws.Count);
        foreach (var seen in new[] { await bobLive.NextAsync(), await ViewAsync(bob, code), await ViewAsync(alice, code) })
        {
            Assert.Equal(
                """{"seat":1,"roll":3,"from":0,"landed":3,"to":3,"play":{"card":"Pump","steps":3,"from":3,"landed":6,"to":6}}""",
                LastMove(seen));
            Assert.Equal("""[{"seat":1,"tile":6,"cards":1},{"seat":2,"tile":0,"cards":0}]""", Pawns(seen));
            Assert.Equal(1, seen.GetProperty("state").GetProperty("discard").GetInt32());
            Assert.Equal((2, "Move"), (TurnSeat(seen), Phase(seen)));
        }

        Assert.Equal("""["Rug"]""", (await ViewAsync(alice, code)).GetProperty("state").GetProperty("hand").GetRawText());
    }

    [PostgresFact]
    public async Task A_card_that_cant_be_played_changes_nothing_and_draws_nothing()
    {
        var alice = await GuestAsync("Alice");
        var bob = await GuestAsync("Bob");
        var code = await StartedAsync(alice, bob);
        await using (var db = database.CreateDbContext())
        {
            await db.Database.ExecuteSqlRawAsync(
                """UPDATE "GameRooms" SET "State" = {0}::jsonb WHERE "Code" = {1}""",
                """{"rulesVersion":1,"pawns":[{"seat":1,"tile":5,"hand":["Whale","Rug"]},{"seat":2,"tile":8,"hand":["Fomo"]}],"deck":[],"discard":[],"lastMove":{"seat":1,"roll":1,"from":4,"landed":5,"to":5},"playPhase":1}""",
                code);
        }

        var room = await ViewAsync(alice, code);
        var before = await StoredAsync(code);
        var drawsBefore = _dice.Draws.Count;

        foreach (var (payload, reason) in new (object, string)[]
        {
            (new { card = "Fomo" }, "card_not_held"),
            (new { card = "Rug" }, "card_not_playable"),
            (new { card = "Joker" }, "invalid_card"),
            (new { card = "Whale", target = 1 }, "invalid_target"),
            (new { card = "Whale" }, "invalid_target"),
        })
        {
            var refused = await PlayCardAsync(alice, room, payload, HttpStatusCode.Conflict);
            Assert.Equal(("command_rejected", reason), (refused.GetProperty("code").GetString(), refused.GetProperty("reason").GetString()));
        }

        var outOfTurn = await PlayCardAsync(bob, room, new { card = "Fomo" }, HttpStatusCode.Conflict);
        Assert.Equal("not_your_turn", outOfTurn.GetProperty("code").GetString());
        Assert.Equal(before, await StoredAsync(code));
        Assert.Equal(drawsBefore, _dice.Draws.Count);

        await PlayCardAsync(alice, room, new { card = "Whale", target = 2 });
        Assert.Equal("""[{"seat":1,"tile":8,"cards":1},{"seat":2,"tile":5,"cards":1}]""", Pawns(await ViewAsync(bob, code)));
    }
}
