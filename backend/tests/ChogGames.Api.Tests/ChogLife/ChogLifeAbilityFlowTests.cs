using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Channels;
using ChogGames.Api.ChogLife;
using ChogGames.Api.Matches;
using ChogGames.Api.Realtime;
using ChogGames.Api.Rooms;
using ChogGames.Core.Abilities;
using ChogGames.Core.Matches;
using ChogGames.Infrastructure.Matches;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ChogGames.Api.Tests.ChogLife;

/// <summary>
/// STORY-042, DEC-044: Chog Life's real abilities in a match. Every player is offered the same four; each works once,
/// only on its player's own turn and in its window; a use keeps the turn and draws nothing, except Event Preview, whose
/// draws the next roll replays. Only the previewing player sees the preview, over REST and live. Refusals change nothing.
/// STORY-091, DEC-078: Double Dice's dice are rolled on use and the roll keeps one of them; Tax the Rich moves MON from the
/// richest opponent.
/// </summary>
public sealed class ChogLifeAbilityFlowTests(MigratedPostgresDatabase database) : IClassFixture<MigratedPostgresDatabase>, IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Empty, it keeps seats in join order; each test queues its draws after the start.</summary>
    private readonly ScriptedRandomSource _dice = new();

    private readonly List<IAsyncDisposable> _disposables = [];

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
                services.AddChogLifeAbilities();
                services.AddSingleton<IRandomSource>(_dice);
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

    /// <summary>A started Chog Life match, seated in join order, in which each player locked the ability given.</summary>
    private static async Task<string> StartedAsync(params (Guest Guest, string Ability)[] players)
    {
        var created = await SendAsync(players[0].Guest, HttpMethod.Post, "/api/rooms", new { gameType = "chog-life" }, HttpStatusCode.Created);
        var code = created.GetProperty("code").GetString()!;
        foreach (var (guest, _) in players[1..])
        {
            await SendAsync(guest, HttpMethod.Post, $"/api/rooms/{code}/join");
        }

        foreach (var (guest, ability) in players)
        {
            await SendAsync(guest, HttpMethod.Put, $"/api/rooms/{code}/ability", new { abilityId = ability });
            await SendAsync(guest, HttpMethod.Put, $"/api/rooms/{code}/ready", new { ready = true });
        }

        await SendAsync(players[0].Guest, HttpMethod.Post, $"/api/rooms/{code}/start");
        return code;
    }

    private static Task<JsonElement> CommandAsync(
        Guest guest, JsonElement room, string type, object? payload = null, HttpStatusCode expected = HttpStatusCode.OK) =>
        SendAsync(guest, HttpMethod.Post, $"/api/rooms/{room.GetProperty("code").GetString()}/commands", new
        {
            actionId = Guid.NewGuid(),
            matchId = room.GetProperty("matchId").GetGuid(),
            expectedVersion = room.GetProperty("version").GetInt64(),
            type,
            payload,
        }, expected);

    private static Task<JsonElement> UseAsync(Guest guest, JsonElement room, string ability, HttpStatusCode expected = HttpStatusCode.OK) =>
        CommandAsync(guest, room, MatchCommand.UseAbility, new { abilityId = ability }, expected);

    /// <summary>Asserts a refusal with the stable command code and, for a refused ability, its reason.</summary>
    private static async Task RefusedAsync(Guest guest, JsonElement room, string ability, string code, string? reason)
    {
        var problem = await UseAsync(guest, room, ability, HttpStatusCode.Conflict);
        Assert.Equal(code, problem.GetProperty("code").GetString());
        Assert.Equal(reason, problem.TryGetProperty("reason", out var given) ? given.GetString() : null);
    }

    private static JsonElement Member(JsonElement room, string name) =>
        room.GetProperty("members").EnumerateArray().Single(m => m.GetProperty("displayName").GetString() == name);

    private static int UsesLeft(JsonElement room, string name) =>
        Member(room, name).GetProperty("ability").GetProperty("usesLeft").GetInt32();

    private static string? TurnOf(JsonElement room) =>
        room.GetProperty("members").EnumerateArray().Single(m => m.GetProperty("isTurn").GetBoolean()).GetProperty("displayName").GetString();

    private static JsonElement Pawn(JsonElement room, int seat) =>
        room.GetProperty("state").GetProperty("pawns").EnumerateArray().Single(p => p.GetProperty("seat").GetInt32() == seat);

    private static string? Armed(JsonElement room, int seat) =>
        Pawn(room, seat).TryGetProperty("armed", out var armed) ? armed.GetString() : null;

    private static string LastMove(JsonElement room) => room.GetProperty("state").GetProperty("lastMove").GetRawText();

    [PostgresFact]
    public async Task Without_holder_rules_every_player_is_offered_the_whole_catalog_and_must_choose_one()
    {
        var alice = await GuestAsync("Alice");
        var bob = await GuestAsync("Bob");
        var created = await SendAsync(alice, HttpMethod.Post, "/api/rooms", new { gameType = "chog-life" }, HttpStatusCode.Created);
        var code = created.GetProperty("code").GetString()!;
        var joined = await SendAsync(bob, HttpMethod.Post, $"/api/rooms/{code}/join");

        string[] offered = ["reward_boost", "loss_protection", "event_preview", "event_reroll", "double_dice", "tax_the_rich"];
        Assert.Equal(offered, created.GetProperty("abilities").EnumerateArray().Select(a => a.GetProperty("id").GetString()));
        Assert.Equal(created.GetProperty("abilities").GetRawText(), joined.GetProperty("abilities").GetRawText());
        var problem = await SendAsync(bob, HttpMethod.Put, $"/api/rooms/{code}/ready", new { ready = true }, HttpStatusCode.Conflict);
        Assert.Equal(RoomEndpoints.ErrorCodes.AbilityRequired, problem.GetProperty("code").GetString());
    }

    [PostgresFact]
    public async Task Reward_boost_works_once_keeps_the_turn_and_boosts_the_next_reward()
    {
        var alice = await GuestAsync("Alice");
        var bob = await GuestAsync("Bob");
        var code = await StartedAsync((alice, "reward_boost"), (bob, "loss_protection"));

        await UseAsync(alice, await ViewAsync(alice, code), "reward_boost");
        var armed = await ViewAsync(bob, code);
        Assert.Equal((0, "Alice", "reward_boost"), (UsesLeft(armed, "Alice"), TurnOf(armed), Armed(armed, 1)));

        await RefusedAsync(alice, await ViewAsync(alice, code), "reward_boost", MatchCommandEndpoints.ErrorCodes.CommandRejected, AbilityRefusal.UsedUp);

        _dice.Enqueue(2);
        await CommandAsync(alice, await ViewAsync(alice, code), "ROLL");
        var rolled = await ViewAsync(bob, code);

        Assert.Equal("""{"seat":1,"roll":2,"from":0,"to":2,"path":[1,2],"change":20,"boost":5}""", LastMove(rolled));
        Assert.Equal((5, 125), (Pawn(rolled, 1).GetProperty("bonuses").GetInt32(), Pawn(rolled, 1).GetProperty("portfolio").GetInt32()));
        Assert.Null(Armed(rolled, 1));
        await RefusedAsync(alice, await ViewAsync(alice, code), "reward_boost", MatchCommandEndpoints.ErrorCodes.NotYourTurn, null);
    }

    [PostgresFact]
    public async Task Refused_uses_change_nothing_and_draw_nothing()
    {
        var alice = await GuestAsync("Alice");
        var bob = await GuestAsync("Bob");
        var code = await StartedAsync((alice, "event_preview"), (bob, "loss_protection"));
        var before = await ViewAsync(alice, code);
        var draws = _dice.Draws.Count;

        await RefusedAsync(alice, before, "event_preview", MatchCommandEndpoints.ErrorCodes.CommandRejected, AbilityRefusal.NotNow);
        await RefusedAsync(alice, before, "reward_boost", MatchCommandEndpoints.ErrorCodes.CommandRejected, AbilityRefusal.NotYourAbility);
        await RefusedAsync(bob, await ViewAsync(bob, code), "loss_protection", MatchCommandEndpoints.ErrorCodes.NotYourTurn, null);

        var after = await ViewAsync(alice, code);
        Assert.Equal(before.GetProperty("version").GetInt64(), after.GetProperty("version").GetInt64());
        Assert.Equal(before.GetProperty("state").GetRawText(), after.GetProperty("state").GetRawText());
        Assert.Equal((1, 1), (UsesLeft(after, "Alice"), UsesLeft(after, "Bob")));
        Assert.Equal(draws, _dice.Draws.Count);
    }

    [PostgresFact]
    public async Task Only_the_previewing_player_sees_their_next_move_and_keeping_the_lane_plays_it_out()
    {
        var alice = await GuestAsync("Alice");
        var bob = await GuestAsync("Bob");
        var code = await StartedAsync((alice, "event_preview"), (bob, "loss_protection"));

        // Alice rolls 6 (a market, 0 gains 10), Bob 1, then Alice 3 onto the first fork (tile 9).
        _dice.Enqueue(6, 0, 1, 3);
        await CommandAsync(alice, await ViewAsync(alice, code), "ROLL");
        await CommandAsync(bob, await ViewAsync(bob, code), "ROLL");
        await CommandAsync(alice, await ViewAsync(alice, code), "ROLL");
        var bobLive = await ConnectAsync(bob, code);

        var draws = _dice.Draws.Count;
        await UseAsync(alice, await ViewAsync(alice, code), "event_preview");
        Assert.Equal(draws, _dice.Draws.Count);
        Assert.Equal("event_preview", Armed(await ViewAsync(bob, code), 1));

        // Choosing Degen draws the next move: die 6, then 10 for the stake (never shown) and 50 for the market on tile 19.
        _dice.Enqueue(6, 10, 50);
        await CommandAsync(alice, await ViewAsync(alice, code), "CHOOSE_LANE", new { lane = "Degen" });

        var forAlice = await ViewAsync(alice, code);
        var forBob = await ViewAsync(bob, code);
        await bobLive.NextAsync();
        var pushed = await bobLive.NextAsync();
        Assert.Equal([(1, 7), (0, 100), (0, 100)], _dice.Draws.Skip(draws));
        Assert.Equal("Alice", TurnOf(forAlice));
        Assert.Equal(
            """{"lane":"Degen","roll":6,"to":19,"path":[14,15,16,17,18,19],"outcome":{"chance":50,"effect":"Loss","amount":0,"percent":10,"minimum":5}}""",
            forAlice.GetProperty("state").GetProperty("preview").GetRawText());
        Assert.All([forBob, pushed], room =>
        {
            Assert.Equal(1, room.GetProperty("state").GetProperty("confirming").GetInt32());
            Assert.DoesNotContain("preview", room.GetProperty("state").GetRawText(), StringComparison.Ordinal);
        });

        await CommandAsync(alice, await ViewAsync(alice, code), "CHOOSE_LANE", new { lane = "Degen" });
        Assert.Equal("Bob", TurnOf(await ViewAsync(bob, code)));
        _dice.Enqueue(1);
        await CommandAsync(bob, await ViewAsync(bob, code), "ROLL");
        draws = _dice.Draws.Count;
        await CommandAsync(alice, await ViewAsync(alice, code), "ROLL");

        // 110 MON less the 50 staked, plus the tripled stake of 150, is 210: the market's 10% loss is 21.
        Assert.Equal(draws, _dice.Draws.Count);
        Assert.Equal(
            """{"seat":1,"roll":6,"from":9,"to":19,"path":[14,15,16,17,18,19],"change":-21,"matured":{"lane":"Degen","stake":50,"payout":150}}""",
            LastMove(await ViewAsync(bob, code)));
    }

    [PostgresFact]
    public async Task Switching_after_a_preview_takes_the_other_lane_blind()
    {
        var alice = await GuestAsync("Alice");
        var bob = await GuestAsync("Bob");
        var code = await StartedAsync((alice, "event_preview"), (bob, "loss_protection"));
        _dice.Enqueue(6, 0, 1, 3);
        await CommandAsync(alice, await ViewAsync(alice, code), "ROLL");
        await CommandAsync(bob, await ViewAsync(bob, code), "ROLL");
        await CommandAsync(alice, await ViewAsync(alice, code), "ROLL");
        await UseAsync(alice, await ViewAsync(alice, code), "event_preview");
        _dice.Enqueue(6, 10, 50);
        await CommandAsync(alice, await ViewAsync(alice, code), "CHOOSE_LANE", new { lane = "Degen" });

        await CommandAsync(alice, await ViewAsync(alice, code), "CHOOSE_LANE", new { lane = "Safe" });

        var switched = await ViewAsync(alice, code);
        Assert.Equal("Bob", TurnOf(switched));
        Assert.False(switched.GetProperty("state").TryGetProperty("preview", out _));
        Assert.True(switched.GetProperty("state").GetProperty("lastMove").GetProperty("switched").GetBoolean());
        Assert.Equal("Safe", Pawn(switched, 1).GetProperty("stake").GetProperty("lane").GetString());
        _dice.Enqueue(1, 1);
        await CommandAsync(bob, await ViewAsync(bob, code), "ROLL");
        await CommandAsync(alice, await ViewAsync(alice, code), "ROLL");
        Assert.Equal("""{"seat":1,"roll":1,"from":9,"to":10,"path":[10],"change":10}""", LastMove(await ViewAsync(bob, code)));
    }

    private static async Task<JsonElement> RejectedAsync(Guest guest, JsonElement room, string type, object? payload, string reason)
    {
        var problem = await CommandAsync(guest, room, type, payload, HttpStatusCode.Conflict);
        Assert.Equal(MatchCommandEndpoints.ErrorCodes.CommandRejected, problem.GetProperty("code").GetString());
        Assert.Equal(reason, problem.GetProperty("reason").GetString());
        return problem;
    }

    [PostgresFact]
    public async Task Double_dice_moves_by_the_die_kept_and_tax_the_rich_takes_from_the_richest()
    {
        var alice = await GuestAsync("Alice");
        var bob = await GuestAsync("Bob");
        var code = await StartedAsync((alice, "double_dice"), (bob, "tax_the_rich"));

        // Bob can't tax on Alice's turn.
        await RefusedAsync(bob, await ViewAsync(bob, code), "tax_the_rich", MatchCommandEndpoints.ErrorCodes.NotYourTurn, null);

        _dice.Enqueue(5);
        _dice.Enqueue(2);
        await UseAsync(alice, await ViewAsync(alice, code), "double_dice");
        var rolled = await ViewAsync(bob, code);
        Assert.Equal(("[5,2]", "Alice", 0), (Pawn(rolled, 1).GetProperty("dice").GetRawText(), TurnOf(rolled), UsesLeft(rolled, "Alice")));

        await RejectedAsync(alice, rolled, "ROLL", null, "choose_die");
        await RejectedAsync(alice, rolled, "ROLL", new { die = 4 }, "invalid_die");
        var draws = _dice.Draws.Count;
        await CommandAsync(alice, await ViewAsync(alice, code), "ROLL", new { die = 2 });
        var moved = await ViewAsync(bob, code);
        Assert.Equal("""{"seat":1,"roll":2,"from":0,"to":2,"path":[1,2],"change":20,"dice":[5,2]}""", LastMove(moved));
        Assert.Equal(draws, _dice.Draws.Count);
        Assert.Equal("Bob", TurnOf(moved));

        await UseAsync(bob, moved, "tax_the_rich");
        var taxed = await ViewAsync(alice, code);
        Assert.Equal((108, 112), (Pawn(taxed, 1).GetProperty("mon").GetInt32(), Pawn(taxed, 2).GetProperty("mon").GetInt32()));
        Assert.Equal("""{"seat":2,"from":1,"amount":12}""", taxed.GetProperty("state").GetProperty("tax").GetRawText());
        Assert.Equal(draws, _dice.Draws.Count);
        await RefusedAsync(bob, taxed, "tax_the_rich", MatchCommandEndpoints.ErrorCodes.CommandRejected, AbilityRefusal.UsedUp);

        _dice.Enqueue(1);
        await CommandAsync(bob, await ViewAsync(bob, code), "ROLL");
        Assert.Equal(
            """{"seat":2,"roll":1,"from":0,"to":1,"path":[1],"taxed":{"seat":2,"from":1,"amount":12}}""",
            LastMove(await ViewAsync(alice, code)));
    }
}
