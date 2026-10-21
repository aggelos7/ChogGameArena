using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ChogGames.Api.ChogRace;
using ChogGames.Api.Matches;
using ChogGames.Core.Matches;
using ChogGames.Infrastructure.Matches;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ChogGames.Api.Tests.ChogRace;

/// <summary>
/// STORY-055, DEC-059: Chog Race's lobby offers its four abilities and a player readies only with one, locked at start.
/// In the match, Card Steal takes a server-drawn card that only the two players see, a Shield blocks an attack with no
/// reply window, and no ability is used while an attack waits for its target's reply or after its one use; a refusal
/// changes nothing. STORY-092, DEC-080: Double Dice moves by the die kept of those the server rolled, and a Frozen opponent
/// is passed over once.
/// </summary>
public sealed class ChogRaceAbilityEndpointTests(MigratedPostgresDatabase database) : IClassFixture<MigratedPostgresDatabase>, IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Empty, it keeps seats in join order; each test queues its draws after the start.</summary>
    private readonly ScriptedRandomSource _dice = new();

    private readonly List<HttpClient> _clients = [];

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
                services.AddChogRaceAbilities();
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
        foreach (var client in _clients)
        {
            client.Dispose();
        }

        await _factory.DisposeAsync();
    }

    private async Task<HttpClient> GuestAsync(string name)
    {
        var client = _factory.CreateClient(
            new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), HandleCookies = false });
        _clients.Add(client);
        using var response = await client.PostAsJsonAsync("/api/guest-sessions", new { displayName = name });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        client.DefaultRequestHeaders.Add("Cookie", response.Headers.GetValues("Set-Cookie").Single().Split(';')[0]);
        return client;
    }

    private static async Task<JsonElement> SendAsync(
        HttpClient client, HttpMethod method, string path, object? body = null, HttpStatusCode expected = HttpStatusCode.OK)
    {
        using var request = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body) };
        using var response = await client.SendAsync(request);
        Assert.Equal(expected, response.StatusCode);
        return response.Content.Headers.ContentLength == 0 ? default : await response.Content.ReadFromJsonAsync<JsonElement>(Json);
    }

    private static Task<JsonElement> ViewAsync(HttpClient client, string code) => SendAsync(client, HttpMethod.Get, $"/api/rooms/{code}");

    private static Task<JsonElement> CommandAsync(
        HttpClient client, JsonElement room, string type, object? payload, HttpStatusCode expected = HttpStatusCode.OK) =>
        SendAsync(client, HttpMethod.Post, $"/api/rooms/{room.GetProperty("code").GetString()}/commands", new
        {
            actionId = Guid.NewGuid(),
            matchId = room.GetProperty("matchId").GetGuid(),
            expectedVersion = room.GetProperty("version").GetInt64(),
            type,
            payload,
        }, expected);

    private static async Task RefusedAsync(Task<JsonElement> command, string reason)
    {
        var refused = await command;
        Assert.Equal(("command_rejected", reason), (refused.GetProperty("code").GetString(), refused.GetProperty("reason").GetString()));
    }

    /// <summary>
    /// A started match of <paramref name="players"/>, seated in join order, each having chosen their ability in the lobby;
    /// returns its code. Readying without an ability is refused first.
    /// </summary>
    private async Task<string> StartedAsync(params (HttpClient Client, string Ability)[] players)
    {
        var created = await SendAsync(players[0].Client, HttpMethod.Post, "/api/rooms", new { gameType = "chog-race" }, HttpStatusCode.Created);
        var code = created.GetProperty("code").GetString()!;
        foreach (var (client, _) in players[1..])
        {
            _clock.Advance(TimeSpan.FromSeconds(1));
            await SendAsync(client, HttpMethod.Post, $"/api/rooms/{code}/join");
        }

        var unready = await SendAsync(players[0].Client, HttpMethod.Put, $"/api/rooms/{code}/ready", new { ready = true }, HttpStatusCode.Conflict);
        Assert.Equal("ability_required", unready.GetProperty("code").GetString());
        foreach (var (client, ability) in players)
        {
            await SendAsync(client, HttpMethod.Put, $"/api/rooms/{code}/ability", new { abilityId = ability });
            await SendAsync(client, HttpMethod.Put, $"/api/rooms/{code}/ready", new { ready = true });
        }

        await SendAsync(players[0].Client, HttpMethod.Post, $"/api/rooms/{code}/start");
        return code;
    }

    private async Task SetStateAsync(string code, string state)
    {
        await using var db = database.CreateDbContext();
        await db.Database.ExecuteSqlRawAsync("""UPDATE "GameRooms" SET "State" = {0}::jsonb WHERE "Code" = {1}""", state, code);
    }

    private async Task<(long Version, string? State, string Uses)> StoredAsync(string code)
    {
        await using var db = database.CreateDbContext();
        var room = await db.GameRooms.AsNoTracking().SingleAsync(r => r.Code == code);
        return (
            room.Version,
            room.State is null ? null : JsonSerializer.Serialize(JsonDocument.Parse(room.State).RootElement),
            string.Join(",", room.Members.OrderBy(m => m.Seat).Select(m => m.AbilityUses)));
    }

    private static string? Hand(JsonElement room) =>
        room.GetProperty("state").TryGetProperty("hand", out var hand) ? hand.GetRawText() : null;

    private static int? UsesLeft(JsonElement room, string name) =>
        room.GetProperty("members").EnumerateArray().Single(m => m.GetProperty("displayName").GetString() == name)
            .GetProperty("ability").GetProperty("usesLeft").GetInt32();

    [PostgresFact]
    public async Task The_lobby_offers_the_six_race_abilities()
    {
        var alice = await GuestAsync("Alice");
        var room = await SendAsync(alice, HttpMethod.Post, "/api/rooms", new { gameType = "chog-race" }, HttpStatusCode.Created);

        Assert.Equal(
            ["movement_boost", "attack_block", "card_preview", "card_steal", "double_dice", "freeze"],
            room.GetProperty("abilities").EnumerateArray().Select(a => a.GetProperty("id").GetString()));
    }

    [PostgresFact]
    public async Task Card_steal_takes_a_server_drawn_card_only_the_two_players_see()
    {
        var alice = await GuestAsync("Alice");
        var bob = await GuestAsync("Bob");
        var carol = await GuestAsync("Carol");
        var code = await StartedAsync((alice, "card_steal"), (bob, "attack_block"), (carol, "card_preview"));
        await SetStateAsync(
            code,
            """{"rulesVersion":1,"pawns":[{"seat":1,"tile":0},{"seat":2,"tile":3,"hand":["Rug","Hodl"]},{"seat":3,"tile":0}],"deck":["Pump"],"discard":[]}""");

        _dice.Enqueue(1);
        await CommandAsync(alice, await ViewAsync(alice, code), MatchCommand.UseAbility, new { abilityId = "card_steal", target = 2 });

        var aliceView = await ViewAsync(alice, code);
        Assert.Equal("""["Hodl"]""", Hand(aliceView));
        Assert.Equal("""["Rug"]""", Hand(await ViewAsync(bob, code)));
        const string Stolen = """{"seat":1,"ability":"card_steal","target":2,"card":"Hodl","cards":["Hodl"],"taken":1}""";
        Assert.Equal(Stolen, aliceView.GetProperty("state").GetProperty("ability").GetRawText());
        Assert.Equal(Stolen, (await ViewAsync(bob, code)).GetProperty("state").GetProperty("ability").GetRawText());
        var carolView = await ViewAsync(carol, code);
        Assert.Equal("""{"seat":1,"ability":"card_steal","target":2,"taken":1}""", carolView.GetProperty("state").GetProperty("ability").GetRawText());
        Assert.DoesNotContain("Hodl", carolView.GetRawText());
        Assert.Equal(0, UsesLeft(aliceView, "Alice"));

        // Its one use is spent; a second is refused and changes nothing.
        var before = await StoredAsync(code);
        await RefusedAsync(
            CommandAsync(alice, aliceView, MatchCommand.UseAbility, new { abilityId = "card_steal", target = 2 }, HttpStatusCode.Conflict),
            "ability_used_up");
        Assert.Equal(before, await StoredAsync(code));
    }

    [PostgresFact]
    public async Task A_shield_blocks_an_attack_with_no_reply_window_and_no_ability_is_used_during_one()
    {
        var alice = await GuestAsync("Alice");
        var bob = await GuestAsync("Bob");
        var code = await StartedAsync((alice, "card_preview"), (bob, "attack_block"));

        // Alice rolls and plays Rug on Bob, who holds a card: the turn waits for his reply, and no ability can be used.
        await SetStateAsync(
            code,
            """{"rulesVersion":1,"pawns":[{"seat":1,"tile":0,"hand":["Rug","Rug"]},{"seat":2,"tile":9,"hand":["Fomo"]}],"deck":["Pump"],"discard":[]}""");
        _dice.Enqueue(1);
        await CommandAsync(alice, await ViewAsync(alice, code), "ROLL", null);
        await CommandAsync(alice, await ViewAsync(alice, code), "PLAY_CARD", new { card = "Rug", target = 2 });
        var waiting = await ViewAsync(bob, code);
        Assert.Equal("Reply", waiting.GetProperty("state").GetProperty("phase").GetString());
        var before = await StoredAsync(code);
        await RefusedAsync(
            CommandAsync(bob, waiting, MatchCommand.UseAbility, new { abilityId = "attack_block" }, HttpStatusCode.Conflict), "reply_pending");
        Assert.Equal(before, await StoredAsync(code));
        await CommandAsync(bob, waiting, "PASS", null);

        // Bob raises his Shield on his turn, for everyone to see, and rolls.
        await CommandAsync(bob, await ViewAsync(bob, code), MatchCommand.UseAbility, new { abilityId = "attack_block" });
        var shielded = await ViewAsync(alice, code);
        Assert.True(shielded.GetProperty("state").GetProperty("pawns")[1].GetProperty("shielded").GetBoolean());
        _dice.Enqueue(1);
        await CommandAsync(bob, shielded, "ROLL", null);
        await CommandAsync(bob, await ViewAsync(bob, code), "END_TURN", null);

        // Alice's next Rug is blocked at once: no reply window, Bob's tile and card unchanged, the turn passes.
        _dice.Enqueue(1);
        await CommandAsync(alice, await ViewAsync(alice, code), "ROLL", null);
        var bobTile = (await ViewAsync(alice, code)).GetProperty("state").GetProperty("pawns")[1].GetProperty("tile").GetInt32();
        await CommandAsync(alice, await ViewAsync(alice, code), "PLAY_CARD", new { card = "Rug", target = 2 });

        var blocked = await ViewAsync(bob, code);
        var state = blocked.GetProperty("state");
        Assert.Equal("Move", state.GetProperty("phase").GetString());
        Assert.Equal(2, blocked.GetProperty("members").EnumerateArray().Single(m => m.GetProperty("isTurn").GetBoolean()).GetProperty("seat").GetInt32());
        Assert.Equal("Shielded", state.GetProperty("lastMove").GetProperty("play").GetProperty("reply").GetString());
        Assert.Equal(bobTile, state.GetProperty("pawns")[1].GetProperty("tile").GetInt32());
        Assert.False(state.GetProperty("pawns")[1].TryGetProperty("shielded", out _));
        Assert.Equal("""["Fomo"]""", Hand(blocked));
    }

    private static int TurnSeat(JsonElement room) =>
        room.GetProperty("members").EnumerateArray().Single(m => m.GetProperty("isTurn").GetBoolean()).GetProperty("seat").GetInt32();

    [PostgresFact]
    public async Task Double_dice_moves_by_the_die_kept_of_those_the_server_rolled()
    {
        var alice = await GuestAsync("Alice");
        var bob = await GuestAsync("Bob");
        var code = await StartedAsync((alice, "double_dice"), (bob, "attack_block"));

        // The dice come from the server, never the payload, and everyone sees them.
        _dice.Enqueue(1, 3);
        await CommandAsync(alice, await ViewAsync(alice, code), MatchCommand.UseAbility, new { abilityId = "double_dice" });
        Assert.Equal("[1,3]", (await ViewAsync(bob, code)).GetProperty("state").GetProperty("pawns")[0].GetProperty("dice").GetRawText());

        // A die not rolled is refused and changes nothing.
        var before = await StoredAsync(code);
        await RefusedAsync(CommandAsync(alice, await ViewAsync(alice, code), "ROLL", new { die = 2 }, HttpStatusCode.Conflict), "invalid_die");
        await RefusedAsync(CommandAsync(alice, await ViewAsync(alice, code), "ROLL", null, HttpStatusCode.Conflict), "choose_die");
        Assert.Equal(before, await StoredAsync(code));

        await CommandAsync(alice, await ViewAsync(alice, code), "ROLL", new { die = 3 });
        var moved = (await ViewAsync(bob, code)).GetProperty("state");
        Assert.Equal(3, moved.GetProperty("pawns")[0].GetProperty("tile").GetInt32());
        Assert.Equal((3, "[1,3]"), (moved.GetProperty("lastMove").GetProperty("roll").GetInt32(), moved.GetProperty("lastMove").GetProperty("dice").GetRawText()));
        Assert.False(moved.GetProperty("pawns")[0].TryGetProperty("dice", out _));
    }

    [PostgresFact]
    public async Task A_frozen_opponent_is_passed_over_once()
    {
        var alice = await GuestAsync("Alice");
        var bob = await GuestAsync("Bob");
        var carol = await GuestAsync("Carol");
        var code = await StartedAsync((alice, "freeze"), (bob, "attack_block"), (carol, "card_preview"));

        await CommandAsync(alice, await ViewAsync(alice, code), MatchCommand.UseAbility, new { abilityId = "freeze", target = 2 });
        var frozen = await ViewAsync(carol, code);
        Assert.Equal("[2]", frozen.GetProperty("state").GetProperty("skipping").GetRawText());
        Assert.Equal("""{"seat":1,"ability":"freeze","target":2}""", frozen.GetProperty("state").GetProperty("ability").GetRawText());
        Assert.Equal(0, UsesLeft(frozen, "Alice"));

        // Alice's turn goes on; her roll ends it and Bob is passed over.
        _dice.Enqueue(1);
        await CommandAsync(alice, await ViewAsync(alice, code), "ROLL", null);
        Assert.Equal(3, TurnSeat(await ViewAsync(carol, code)));
        _dice.Enqueue(1);
        await CommandAsync(carol, await ViewAsync(carol, code), "ROLL", null);
        Assert.Equal(1, TurnSeat(await ViewAsync(carol, code)));
        _dice.Enqueue(3);
        await CommandAsync(alice, await ViewAsync(alice, code), "ROLL", null);
        Assert.Equal(2, TurnSeat(await ViewAsync(bob, code)));
    }

    [PostgresFact]
    public async Task Freeze_on_a_player_already_skipping_or_who_left_is_refused_and_changes_nothing()
    {
        var alice = await GuestAsync("Alice");
        var bob = await GuestAsync("Bob");
        var carol = await GuestAsync("Carol");
        var code = await StartedAsync((alice, "freeze"), (bob, "attack_block"), (carol, "card_preview"));

        // Carol plays Liquidated on Bob, who holds nothing, so he is still due to skip on Alice's turn; then Carol leaves.
        await SetStateAsync(
            code,
            """{"rulesVersion":1,"pawns":[{"seat":1,"tile":0},{"seat":2,"tile":0},{"seat":3,"tile":0,"hand":["Liquidated"]}],"deck":["Pump"],"discard":[]}""");
        _dice.Enqueue(1, 1, 1);
        await CommandAsync(alice, await ViewAsync(alice, code), "ROLL", null);
        await CommandAsync(bob, await ViewAsync(bob, code), "ROLL", null);
        await CommandAsync(carol, await ViewAsync(carol, code), "ROLL", null);
        await CommandAsync(carol, await ViewAsync(carol, code), "PLAY_CARD", new { card = "Liquidated", target = 2 });
        Assert.Equal(1, TurnSeat(await ViewAsync(alice, code)));
        await SendAsync(carol, HttpMethod.Post, $"/api/rooms/{code}/leave", expected: HttpStatusCode.NoContent);

        var before = await StoredAsync(code);
        await RefusedAsync(
            CommandAsync(alice, await ViewAsync(alice, code), MatchCommand.UseAbility, new { abilityId = "freeze", target = 2 }, HttpStatusCode.Conflict),
            "already_skipping");
        await RefusedAsync(
            CommandAsync(alice, await ViewAsync(alice, code), MatchCommand.UseAbility, new { abilityId = "freeze", target = 3 }, HttpStatusCode.Conflict),
            "invalid_target");
        Assert.Equal(before, await StoredAsync(code));
    }
}
