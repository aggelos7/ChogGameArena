using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Channels;
using ChogGames.Api.Realtime;
using ChogGames.Core.Matches;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ChogGames.Api.Tests.Realtime;

/// <summary>Versioned snapshots pushed to every member, resync on subscribing and private game state (STORY-026, DEC-028).</summary>
public sealed class RoomSyncTests(MigratedPostgresDatabase database) : IClassFixture<MigratedPostgresDatabase>, IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static readonly TimeSpan Turn = TimeSpan.FromSeconds(30);

    private readonly ManualTimeProvider _clock = new(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
    private readonly List<IAsyncDisposable> _disposables = [];
    private ApiFactory _factory = null!;

    public async Task InitializeAsync()
    {
        if (database.ConnectionString.Length > 0)
        {
            await using var db = database.CreateDbContext();
            await db.Database.ExecuteSqlRawAsync("TRUNCATE \"GuestSessions\", \"Players\" CASCADE");
        }

        // Chog Life plays the secret game below; Chog Race has no rules. The scheduler runs, on the test's clock.
        _factory = new ApiFactory(
            database.ConnectionString,
            new Dictionary<string, string?>
            {
                ["Games:Items:0:Status"] = "Available",
                ["Games:Items:1:Status"] = "Available",
                ["Matches:TurnTimeout"] = Turn.ToString(),
                ["Matches:TimeoutCheckInterval"] = "00:00:00.1",
            },
            services =>
            {
                services.AddSingleton<TimeProvider>(_clock);
                services.RemoveAll<IMatchRules>().AddSingleton<IMatchRules, SecretRules>();
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
        using var response = await client.PostAsJsonAsync("/api/guest-sessions", new { displayName = name });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var cookie = response.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
        client.DefaultRequestHeaders.Add("Cookie", cookie);
        return new Guest(client, cookie);
    }

    /// <summary>One live connection and the snapshots it receives.</summary>
    private sealed class Listener(HubConnection connection) : IAsyncDisposable
    {
        private readonly Channel<JsonElement> _snapshots = Channel.CreateUnbounded<JsonElement>();

        public void Listen() => connection.On<JsonElement>(nameof(IRoomClient.RoomUpdated), room => _snapshots.Writer.TryWrite(room));

        public Task StartAsync() => connection.StartAsync();

        public Task<JsonElement> SubscribeAsync(string code) => connection.InvokeAsync<JsonElement>(nameof(RoomHub.SubscribeRoom), code);

        public Task UnsubscribeAsync(string code) => connection.InvokeAsync(nameof(RoomHub.UnsubscribeRoom), code);

        public async Task<JsonElement> NextAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            return await _snapshots.Reader.ReadAsync(timeout.Token);
        }

        /// <summary>Called once the same change has reached a member, so the server has already pushed it.</summary>
        public async Task AssertNothingReceivedAsync()
        {
            await Task.Delay(TimeSpan.FromMilliseconds(300));
            Assert.False(_snapshots.Reader.TryRead(out var room), $"Unexpected snapshot: {room}.");
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

    /// <summary>A started match of <paramref name="players"/>, the first hosting and playing first; returns its code.</summary>
    private static async Task<string> StartedAsync(string gameType, params Guest[] players)
    {
        var created = await SendAsync(players[0], HttpMethod.Post, "/api/rooms", new { gameType }, HttpStatusCode.Created);
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

    private static long Version(JsonElement room) => room.GetProperty("version").GetInt64();

    private static string? TurnOf(JsonElement room) =>
        room.GetProperty("members").EnumerateArray().SingleOrDefault(m => m.GetProperty("isTurn").GetBoolean()) is { ValueKind: JsonValueKind.Object } member
            ? member.GetProperty("displayName").GetString()
            : null;

    /// <summary>The room as everyone sees it: the snapshot without who is looking (<c>isYou</c>) or private views.</summary>
    private static string Shared(JsonElement room)
    {
        var members = room.GetProperty("members").EnumerateArray().Select(m => new
        {
            name = m.GetProperty("displayName").GetString(),
            ready = m.GetProperty("isReady").GetBoolean(),
            left = m.GetProperty("hasLeft").GetBoolean(),
            turn = m.GetProperty("isTurn").GetBoolean(),
        });
        var state = room.GetProperty("state");
        return JsonSerializer.Serialize(new
        {
            version = Version(room),
            status = room.GetProperty("status").GetString(),
            turnNumber = room.GetProperty("turnNumber").GetInt32(),
            turnEndsAt = room.GetProperty("turnEndsAt").ToString(),
            count = state.ValueKind == JsonValueKind.Object ? state.GetProperty("count").GetInt32() : (int?)null,
            members,
        });
    }

    private static void AssertYou(JsonElement room, string name) =>
        Assert.Equal(name, room.GetProperty("members").EnumerateArray().Single(m => m.GetProperty("isYou").GetBoolean()).GetProperty("displayName").GetString());

    [PostgresFact]
    public async Task Two_players_converge_on_the_same_state_after_every_command()
    {
        var alice = await GuestAsync("Alice");
        var bob = await GuestAsync("Bob");
        var code = await StartedAsync("chog-life", alice, bob);
        var aliceLive = await ConnectAsync(alice);
        var bobLive = await ConnectAsync(bob);

        // Subscribing answers with the current snapshot, the same for both apart from who is looking.
        var aliceRoom = await aliceLive.SubscribeAsync(code);
        var bobRoom = await bobLive.SubscribeAsync(code);
        Assert.Equal(Shared(aliceRoom), Shared(bobRoom));
        AssertYou(aliceRoom, "Alice");
        AssertYou(bobRoom, "Bob");
        Assert.Equal(JsonValueKind.Null, aliceRoom.GetProperty("state").ValueKind);

        var turns = new[] { (alice, "Alice", "Bob"), (bob, "Bob", "Alice"), (alice, "Alice", "Bob") };
        for (var i = 0; i < turns.Length; i++)
        {
            var (player, name, next) = turns[i];
            var before = name == "Alice" ? aliceRoom : bobRoom;
            var accepted = await CommandAsync(player, before, "ADD");

            aliceRoom = await aliceLive.NextAsync();
            bobRoom = await bobLive.NextAsync();
            Assert.Equal(Shared(aliceRoom), Shared(bobRoom));
            Assert.Equal(accepted.GetProperty("version").GetInt64(), Version(aliceRoom));
            Assert.Equal(Version(before) + 1, Version(aliceRoom));
            Assert.Equal(i + 1, aliceRoom.GetProperty("state").GetProperty("count").GetInt32());
            Assert.Equal(next, TurnOf(aliceRoom));
            AssertYou(aliceRoom, "Alice");
            AssertYou(bobRoom, "Bob");

            // Pushed snapshots are exactly what a fetch returns.
            Assert.Equal(aliceRoom.ToString(), (await ViewAsync(alice, code)).ToString());
            Assert.Equal(bobRoom.ToString(), (await ViewAsync(bob, code)).ToString());
        }
    }

    [PostgresFact]
    public async Task Every_lobby_change_is_pushed_as_a_versioned_snapshot()
    {
        var alice = await GuestAsync("Alice");
        var bob = await GuestAsync("Bob");
        var created = await SendAsync(alice, HttpMethod.Post, "/api/rooms", new { gameType = "chog-life" }, HttpStatusCode.Created);
        var code = created.GetProperty("code").GetString()!;
        var aliceLive = await ConnectAsync(alice);
        Assert.Equal(0, Version(await aliceLive.SubscribeAsync(code)));

        await SendAsync(bob, HttpMethod.Post, $"/api/rooms/{code}/join");
        var joined = await aliceLive.NextAsync();
        Assert.Equal((1L, 2), (Version(joined), joined.GetProperty("members").GetArrayLength()));

        var bobLive = await ConnectAsync(bob);
        await bobLive.SubscribeAsync(code);
        var ready = await SendAsync(bob, HttpMethod.Put, $"/api/rooms/{code}/ready", new { ready = true });
        var aliceSees = await aliceLive.NextAsync();
        var bobSees = await bobLive.NextAsync();
        Assert.Equal((2L, 2L, 2L), (Version(ready), Version(aliceSees), Version(bobSees)));
        Assert.Equal(Shared(aliceSees), Shared(bobSees));
    }

    [PostgresFact]
    public async Task A_player_who_missed_versions_resyncs_by_subscribing_again()
    {
        var alice = await GuestAsync("Alice");
        var bob = await GuestAsync("Bob");
        var code = await StartedAsync("chog-life", alice, bob);
        var aliceLive = await ConnectAsync(alice);
        var room = await aliceLive.SubscribeAsync(code);
        var bobLive = await ConnectAsync(bob);
        var bobRoom = await bobLive.SubscribeAsync(code);

        // Bob's connection drops; two versions go by without him.
        await bobLive.DisposeAsync();
        await CommandAsync(alice, room, "ADD");
        room = await aliceLive.NextAsync();
        await CommandAsync(bob, room, "ADD");
        room = await aliceLive.NextAsync();
        Assert.Equal(Version(bobRoom) + 2, Version(room));

        // A new connection subscribing gets the current snapshot, gap closed, and the next change after it.
        var bobAgain = await ConnectAsync(bob);
        var resynced = await bobAgain.SubscribeAsync(code);
        Assert.Equal(Shared(room), Shared(resynced));

        await CommandAsync(alice, room, "ADD");
        Assert.Equal(Shared(await aliceLive.NextAsync()), Shared(await bobAgain.NextAsync()));

        // Unsubscribing and subscribing again on the same connection resyncs too.
        await bobAgain.UnsubscribeAsync(code);
        room = await ViewAsync(alice, code);
        await CommandAsync(bob, room, "ADD");
        room = await aliceLive.NextAsync();
        await bobAgain.AssertNothingReceivedAsync();
        Assert.Equal(Shared(room), Shared(await bobAgain.SubscribeAsync(code)));
    }

    [PostgresFact]
    public async Task Timed_out_turns_are_pushed_to_every_player()
    {
        var alice = await GuestAsync("Alice");
        var bob = await GuestAsync("Bob");
        var code = await StartedAsync("chog-life", alice, bob);
        var aliceLive = await ConnectAsync(alice);
        var bobLive = await ConnectAsync(bob);
        var before = await aliceLive.SubscribeAsync(code);
        await bobLive.SubscribeAsync(code);

        _clock.Advance(Turn);

        var aliceRoom = await aliceLive.NextAsync();
        var bobRoom = await bobLive.NextAsync();
        Assert.Equal(Shared(aliceRoom), Shared(bobRoom));
        Assert.Equal((Version(before) + 1, "Bob"), (Version(aliceRoom), TurnOf(aliceRoom)));
    }

    [PostgresFact]
    public async Task Each_player_sees_only_their_own_private_state()
    {
        var alice = await GuestAsync("Alice");
        var bob = await GuestAsync("Bob");
        var code = await StartedAsync("chog-life", alice, bob);
        var aliceLive = await ConnectAsync(alice);
        var bobLive = await ConnectAsync(bob);
        var room = await aliceLive.SubscribeAsync(code);
        await bobLive.SubscribeAsync(code);

        await CommandAsync(alice, room, "HIDE", new { secret = 4242 });
        var aliceRoom = await aliceLive.NextAsync();
        var bobRoom = await bobLive.NextAsync();
        Assert.Equal(Shared(aliceRoom), Shared(bobRoom));
        Assert.Equal(4242, aliceRoom.GetProperty("state").GetProperty("mySecret").GetInt32());
        Assert.Equal(JsonValueKind.Null, bobRoom.GetProperty("state").GetProperty("mySecret").ValueKind);

        // Neither the hidden value nor how the game stores it reaches Bob, pushed or fetched.
        foreach (var bobView in new[] { bobRoom, await ViewAsync(bob, code) })
        {
            Assert.DoesNotContain("4242", bobView.ToString());
            Assert.DoesNotContain("secrets", bobView.ToString());
        }
    }

    [PostgresFact]
    public async Task A_game_without_rules_never_shows_its_state()
    {
        var alice = await GuestAsync("Alice");
        var bob = await GuestAsync("Bob");
        var code = await StartedAsync("chog-race", alice, bob);
        await using (var db = database.CreateDbContext())
        {
            const string state = """{"hidden": 1}""";
            await db.Database.ExecuteSqlInterpolatedAsync($"""UPDATE "GameRooms" SET "State" = CAST({state} AS jsonb) WHERE "Code" = {code}""");
        }

        var aliceLive = await ConnectAsync(alice);
        Assert.Equal(JsonValueKind.Null, (await aliceLive.SubscribeAsync(code)).GetProperty("state").ValueKind);
        Assert.Equal(JsonValueKind.Null, (await ViewAsync(alice, code)).GetProperty("state").ValueKind);
    }

    [PostgresFact]
    public async Task Refused_commands_push_nothing_and_change_nothing()
    {
        var alice = await GuestAsync("Alice");
        var bob = await GuestAsync("Bob");
        var mallory = await GuestAsync("Mallory");
        var code = await StartedAsync("chog-life", alice, bob);
        var aliceLive = await ConnectAsync(alice);
        var room = await aliceLive.SubscribeAsync(code);
        var malloryLive = await ConnectAsync(mallory);
        await Assert.ThrowsAsync<HubException>(() => malloryLive.SubscribeAsync(code));

        await CommandAsync(bob, room, "ADD", expected: HttpStatusCode.Conflict); // not Bob's turn
        await CommandAsync(alice, room, "NOPE", expected: HttpStatusCode.BadRequest);
        await CommandAsync(mallory, room, "ADD", expected: HttpStatusCode.NotFound);
        await aliceLive.AssertNothingReceivedAsync();
        Assert.Equal(Version(room), Version(await ViewAsync(alice, code)));

        // The next accepted change still reaches members only.
        await CommandAsync(alice, room, "ADD");
        Assert.Equal(Version(room) + 1, Version(await aliceLive.NextAsync()));
        await malloryLive.AssertNothingReceivedAsync();
    }

    [PostgresFact]
    public async Task A_player_who_forfeits_gets_one_last_snapshot_then_nothing()
    {
        var alice = await GuestAsync("Alice");
        var bob = await GuestAsync("Bob");
        var carol = await GuestAsync("Carol");
        var code = await StartedAsync("chog-life", alice, bob, carol);
        var aliceLive = await ConnectAsync(alice);
        var room = await aliceLive.SubscribeAsync(code);
        var bobLive = await ConnectAsync(bob);
        await bobLive.SubscribeAsync(code);

        await SendAsync(bob, HttpMethod.Post, $"/api/rooms/{code}/leave", expected: HttpStatusCode.NoContent);
        var aliceRoom = await aliceLive.NextAsync();
        var bobLast = await bobLive.NextAsync();
        Assert.Equal(Shared(aliceRoom), Shared(bobLast));
        Assert.True(bobLast.GetProperty("members").EnumerateArray().Single(m => m.GetProperty("isYou").GetBoolean()).GetProperty("hasLeft").GetBoolean());

        await CommandAsync(alice, aliceRoom, "ADD");
        Assert.Equal(Version(room) + 2, Version(await aliceLive.NextAsync()));
        await bobLive.AssertNothingReceivedAsync();
        await Assert.ThrowsAsync<HubException>(() => bobLive.SubscribeAsync(code));
    }

    /// <summary>
    /// A counter everyone sees, plus a secret per player that only they may see. <c>ADD</c> ends the turn; <c>HIDE</c>
    /// stores the player's secret. The stored state keys secrets by player id, which no view ever shows.
    /// </summary>
    private sealed class SecretRules : IMatchRules
    {
        public string GameType => "chog-life";

        public MatchRuleDecision Apply(MatchCommandContext context) => context.Type switch
        {
            "ADD" => MatchRuleDecision.Accept(With(context.State, count: Count(context.State) + 1), endsTurn: true),
            "HIDE" => MatchRuleDecision.Accept(
                With(context.State, secret: (context.PlayerId, context.Payload.GetProperty("secret").GetInt32()))),
            _ => MatchRuleDecision.Unknown,
        };

        public JsonElement? TimeOutTurn(MatchTurnContext context) => null;

        public JsonElement? ViewFor(MatchViewContext context)
        {
            var secrets = context.State.GetProperty("secrets");
            int? mine = secrets.TryGetProperty(context.PlayerId.ToString(), out var secret) ? secret.GetInt32() : null;
            return JsonSerializer.SerializeToElement(new { count = Count(context.State), mySecret = mine });
        }

        private static int Count(JsonElement? state) => state?.GetProperty("count").GetInt32() ?? 0;

        private static JsonElement With(JsonElement? state, int? count = null, (Guid PlayerId, int Value)? secret = null)
        {
            var secrets = state?.GetProperty("secrets").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetInt32()) ?? [];
            if (secret is { } hidden)
            {
                secrets[hidden.PlayerId.ToString()] = hidden.Value;
            }

            return JsonSerializer.SerializeToElement(new { count = count ?? Count(state), secrets });
        }
    }
}
