using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ChogGames.Api.Matches;
using ChogGames.Api.Realtime;
using ChogGames.Core.Matches;
using ChogGames.Infrastructure.Matches;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ChogGames.Api.Tests.Realtime;

/// <summary>
/// Disconnect and reconnect (STORY-027, ENG-006, DEC-029): the same session finds its open room again, a new connection
/// resyncs to the current snapshot, other players can't take a seat over, and being away never earns a turn.
/// </summary>
public sealed class ReconnectTests(MigratedPostgresDatabase database) : IClassFixture<MigratedPostgresDatabase>, IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static readonly TimeSpan Turn = TimeSpan.FromSeconds(30);

    // Whole seconds, so times read back from Postgres (microseconds) compare equal.
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

        // Chog Life plays the turn game below. The tests time out turns themselves.
        _factory = new ApiFactory(
            database.ConnectionString,
            new Dictionary<string, string?>
            {
                ["Games:Items:0:Status"] = "Available",
                ["Games:Items:1:Status"] = "Available",
                ["Matches:TurnTimeout"] = Turn.ToString(),
            },
            services =>
            {
                services.AddSingleton<TimeProvider>(_clock);
                services.RemoveAll<IMatchRules>().AddSingleton<IMatchRules, TurnRules>();
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

    /// <summary>A browser with a guest session; the cookie is kept so a reload or a live connection can send it too.</summary>
    private sealed record Guest(HttpClient Client, string Cookie);

    private HttpClient Browser(string? cookie)
    {
        var client = _factory.CreateClient(
            new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), HandleCookies = false });
        _disposables.Add(new DisposableClient(client));
        if (cookie is not null)
        {
            client.DefaultRequestHeaders.Add("Cookie", cookie);
        }

        return client;
    }

    private sealed class DisposableClient(HttpClient client) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            client.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private async Task<Guest> GuestAsync(string name)
    {
        var client = Browser(cookie: null);
        using var response = await client.PostAsJsonAsync("/api/guest-sessions", new { displayName = name });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var cookie = response.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
        client.DefaultRequestHeaders.Add("Cookie", cookie);
        return new Guest(client, cookie);
    }

    /// <summary>The same player after a browser refresh: a new client that only has the session cookie.</summary>
    private Guest Reloaded(Guest guest) => guest with { Client = Browser(guest.Cookie) };

    private async Task<HubConnection> ConnectAsync(Guest guest)
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
        _disposables.Add(connection);
        await connection.StartAsync();
        return connection;
    }

    private static Task<JsonElement> SubscribeAsync(HubConnection connection, string code) =>
        connection.InvokeAsync<JsonElement>(nameof(RoomHub.SubscribeRoom), code);

    private static async Task<JsonElement> SendAsync(Guest guest, HttpMethod method, string path, object? body = null, HttpStatusCode expected = HttpStatusCode.OK)
    {
        using var request = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body) };
        using var response = await guest.Client.SendAsync(request);
        Assert.Equal(expected, response.StatusCode);
        return response.Content.Headers.ContentLength == 0 ? default : await response.Content.ReadFromJsonAsync<JsonElement>(Json);
    }

    private static Task<JsonElement> ViewAsync(Guest guest, string code) => SendAsync(guest, HttpMethod.Get, $"/api/rooms/{code}");

    private static Task<JsonElement> CurrentAsync(Guest guest, HttpStatusCode expected = HttpStatusCode.OK) =>
        SendAsync(guest, HttpMethod.Get, "/api/rooms/current", expected: expected);

    private static async Task<string> CreatedAsync(Guest host, string gameType = "chog-life") =>
        (await SendAsync(host, HttpMethod.Post, "/api/rooms", new { gameType }, HttpStatusCode.Created)).GetProperty("code").GetString()!;

    /// <summary>A started match of <paramref name="players"/>, the first hosting and playing first; returns its code.</summary>
    private static async Task<string> StartedAsync(params Guest[] players)
    {
        var code = await CreatedAsync(players[0]);
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

    private static Task<JsonElement> CommandAsync(Guest guest, JsonElement room, string type, HttpStatusCode expected = HttpStatusCode.OK) =>
        SendAsync(guest, HttpMethod.Post, $"/api/rooms/{room.GetProperty("code").GetString()}/commands", new
        {
            actionId = Guid.NewGuid(),
            matchId = room.GetProperty("matchId").GetGuid(),
            expectedVersion = room.GetProperty("version").GetInt64(),
            type,
        }, expected);

    private async Task<IReadOnlyList<TimedOutTurn>> SweepAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<TurnTimeoutService>().TimeOutOverdueTurnsAsync(CancellationToken.None);
    }

    private static string Code(JsonElement room) => room.GetProperty("code").GetString()!;

    private static long Version(JsonElement room) => room.GetProperty("version").GetInt64();

    private static string? TurnOf(JsonElement room) =>
        room.GetProperty("members").EnumerateArray().SingleOrDefault(m => m.GetProperty("isTurn").GetBoolean()) is { ValueKind: JsonValueKind.Object } member
            ? member.GetProperty("displayName").GetString()
            : null;

    private static string? ErrorCode(JsonElement problem) => problem.GetProperty("code").GetString();

    [PostgresFact]
    public async Task A_player_in_no_room_has_no_current_room()
    {
        var alice = await GuestAsync("Alice");

        await CurrentAsync(alice, HttpStatusCode.NoContent);
    }

    [PostgresFact]
    public async Task Without_a_session_there_is_no_current_room()
    {
        using var response = await Browser(cookie: null).GetAsync("/api/rooms/current");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [PostgresFact]
    public async Task Host_and_member_find_their_waiting_room_after_a_refresh()
    {
        var alice = await GuestAsync("Alice");
        var bob = await GuestAsync("Bob");
        var code = await CreatedAsync(alice);
        await SendAsync(bob, HttpMethod.Post, $"/api/rooms/{code}/join");
        await SendAsync(bob, HttpMethod.Put, $"/api/rooms/{code}/ready", new { ready = true });

        var forAlice = await CurrentAsync(Reloaded(alice));
        var forBob = await CurrentAsync(Reloaded(bob));

        Assert.Equal(JsonSerializer.Serialize(await ViewAsync(alice, code)), JsonSerializer.Serialize(forAlice));
        Assert.Equal(JsonSerializer.Serialize(await ViewAsync(bob, code)), JsonSerializer.Serialize(forBob));
        Assert.Equal("Waiting", forBob.GetProperty("status").GetString());
    }

    [PostgresFact]
    public async Task A_refresh_returns_the_active_match_with_the_locked_chog_and_current_state()
    {
        var alice = await GuestAsync("Alice");
        var bob = await GuestAsync("Bob");
        var code = await StartedAsync(alice, bob);
        await CommandAsync(alice, await ViewAsync(alice, code), "ADD");
        var before = await ViewAsync(bob, code);

        var reloaded = Reloaded(bob);
        var current = await CurrentAsync(reloaded);

        // Identity, membership, the locked Chog and the authoritative state, exactly as before the refresh.
        Assert.Equal(JsonSerializer.Serialize(before), JsonSerializer.Serialize(current));
        Assert.Equal(("Active", 1), (current.GetProperty("status").GetString(), current.GetProperty("state").GetProperty("count").GetInt32()));
        var you = current.GetProperty("members").EnumerateArray().Single(m => m.GetProperty("isYou").GetBoolean());
        Assert.Equal(("Bob", false), (you.GetProperty("displayName").GetString(), you.GetProperty("hasLeft").GetBoolean()));
    }

    [PostgresFact]
    public async Task A_forfeited_or_ended_match_is_no_longer_current()
    {
        var alice = await GuestAsync("Alice");
        var bob = await GuestAsync("Bob");
        var carol = await GuestAsync("Carol");
        var code = await StartedAsync(alice, bob, carol);

        await SendAsync(carol, HttpMethod.Post, $"/api/rooms/{code}/leave", expected: HttpStatusCode.NoContent);
        await CurrentAsync(carol, HttpStatusCode.NoContent);
        Assert.Equal(code, Code(await CurrentAsync(alice)));

        // Bob leaving leaves Alice alone: the match is abandoned.
        await SendAsync(bob, HttpMethod.Post, $"/api/rooms/{code}/leave", expected: HttpStatusCode.NoContent);
        await CurrentAsync(alice, HttpStatusCode.NoContent);
    }

    [PostgresFact]
    public async Task A_cancelled_room_is_no_longer_current()
    {
        var alice = await GuestAsync("Alice");
        var bob = await GuestAsync("Bob");
        var code = await CreatedAsync(alice);
        await SendAsync(bob, HttpMethod.Post, $"/api/rooms/{code}/join");

        await SendAsync(alice, HttpMethod.Post, $"/api/rooms/{code}/leave", expected: HttpStatusCode.NoContent);

        await CurrentAsync(alice, HttpStatusCode.NoContent);
        await CurrentAsync(bob, HttpStatusCode.NoContent);
    }

    [PostgresFact]
    public async Task Another_player_cannot_take_over_a_seat()
    {
        var alice = await GuestAsync("Alice");
        var bob = await GuestAsync("Bob");
        var mallory = await GuestAsync("Mallory");
        var code = await StartedAsync(alice, bob);
        var before = await ViewAsync(alice, code);

        // Not the room's member: no current room, no snapshot, no subscription, no command, no join.
        await CurrentAsync(mallory, HttpStatusCode.NoContent);
        Assert.Equal("room_not_found", ErrorCode(await SendAsync(mallory, HttpMethod.Get, $"/api/rooms/{code}", expected: HttpStatusCode.NotFound)));
        var malloryLive = await ConnectAsync(mallory);
        var refused = await Assert.ThrowsAsync<HubException>(() => SubscribeAsync(malloryLive, code));
        Assert.Contains(RoomHub.ErrorCodes.RoomNotFound, refused.Message);
        Assert.Equal("room_not_found", ErrorCode(await CommandAsync(mallory, before, "ADD", HttpStatusCode.NotFound)));
        Assert.Equal("room_started", ErrorCode(await SendAsync(mallory, HttpMethod.Post, $"/api/rooms/{code}/join", expected: HttpStatusCode.Conflict)));

        Assert.Equal(JsonSerializer.Serialize(before), JsonSerializer.Serialize(await ViewAsync(alice, code)));
    }

    [PostgresFact]
    public async Task A_new_connection_resyncs_to_everything_missed_while_away()
    {
        var alice = await GuestAsync("Alice");
        var bob = await GuestAsync("Bob");
        var code = await StartedAsync(alice, bob);
        var bobLive = await ConnectAsync(bob);
        await SubscribeAsync(bobLive, code);

        // Bob's connection drops; meanwhile Alice plays and ends her turn, and Bob's turn runs out.
        await bobLive.StopAsync();
        await CommandAsync(alice, await ViewAsync(alice, code), "ADD");
        await CommandAsync(alice, await ViewAsync(alice, code), "END");
        _clock.Advance(Turn);
        Assert.Single(await SweepAsync());

        var resynced = await SubscribeAsync(await ConnectAsync(Reloaded(bob)), code);

        Assert.Equal(JsonSerializer.Serialize(await ViewAsync(bob, code)), JsonSerializer.Serialize(resynced));
        Assert.Equal((3, "Alice"), (resynced.GetProperty("turnNumber").GetInt32(), TurnOf(resynced)));
        Assert.Equal(1, resynced.GetProperty("state").GetProperty("timeouts").GetInt32());
    }

    [PostgresFact]
    public async Task Reconnecting_does_not_extend_the_turn_or_change_the_room()
    {
        var alice = await GuestAsync("Alice");
        var bob = await GuestAsync("Bob");
        var code = await StartedAsync(alice, bob);
        var aliceLive = await ConnectAsync(alice);
        var before = await SubscribeAsync(aliceLive, code);
        _clock.Advance(Turn - TimeSpan.FromSeconds(5));

        // Alice drops and comes back several times on her own turn.
        await aliceLive.StopAsync();
        var reloaded = Reloaded(alice);
        await CurrentAsync(reloaded);
        await ViewAsync(reloaded, code);
        var after = await SubscribeAsync(await ConnectAsync(reloaded), code);

        // Only the server's time moved on (STORY-083).
        Assert.Equal(WithoutServerTime(before), WithoutServerTime(after));
        Assert.Equal(_clock.Now, after.GetProperty("serverTime").GetDateTimeOffset());
        Assert.Equal(_clock.Now + TimeSpan.FromSeconds(5), after.GetProperty("turnEndsAt").GetDateTimeOffset());

        // The original deadline still holds: once it passes, her command is refused and the turn times out as usual.
        _clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal("turn_expired", ErrorCode(await CommandAsync(reloaded, after, "ADD", HttpStatusCode.Conflict)));
        Assert.Single(await SweepAsync());
        var next = await ViewAsync(reloaded, code);
        Assert.Equal((2, "Bob", Version(before) + 1), (next.GetProperty("turnNumber").GetInt32(), TurnOf(next), Version(next)));
    }

    /// <summary>The snapshot as JSON without its <c>serverTime</c>, which every snapshot sets to when it was built.</summary>
    private static string WithoutServerTime(JsonElement snapshot)
    {
        var room = System.Text.Json.Nodes.JsonNode.Parse(snapshot.GetRawText())!.AsObject();
        room.Remove("serverTime");
        return room.ToJsonString();
    }

    [PostgresFact]
    public async Task A_player_back_after_missing_their_turn_cannot_act_on_it()
    {
        var alice = await GuestAsync("Alice");
        var bob = await GuestAsync("Bob");
        var code = await StartedAsync(alice, bob);
        var seenBeforeDrop = await ViewAsync(alice, code);

        // Alice is away when her turn runs out; the game's safe default plays it and Bob is up.
        _clock.Advance(Turn);
        Assert.Single(await SweepAsync());

        // Back with what she saw before dropping: stale. With the resynced version: not her turn.
        var reloaded = Reloaded(alice);
        Assert.Equal("stale_version", ErrorCode(await CommandAsync(reloaded, seenBeforeDrop, "ADD", HttpStatusCode.Conflict)));
        var current = await CurrentAsync(reloaded);
        Assert.Equal("not_your_turn", ErrorCode(await CommandAsync(reloaded, current, "ADD", HttpStatusCode.Conflict)));
        Assert.Equal("not_your_turn", ErrorCode(await CommandAsync(reloaded, current, "END", HttpStatusCode.Conflict)));

        var after = await ViewAsync(bob, code);
        Assert.Equal((Version(current), "Bob", 0), (Version(after), TurnOf(after), after.GetProperty("state").GetProperty("count").GetInt32()));
    }

    /// <summary>
    /// A stand-in game: <c>ADD</c> adds one to a counter and keeps the turn, <c>END</c> ends it, and a turn that runs out
    /// counts a timeout, so a test can see who played what.
    /// </summary>
    private sealed class TurnRules : IMatchRules
    {
        public string GameType => "chog-life";

        public MatchRuleDecision Apply(MatchCommandContext context) => context.Type switch
        {
            "ADD" => MatchRuleDecision.Accept(With(context.State, count: Count(context.State) + 1)),
            "END" => MatchRuleDecision.Accept(With(context.State), endsTurn: true),
            _ => MatchRuleDecision.Unknown,
        };

        public JsonElement? TimeOutTurn(MatchTurnContext context) => With(context.State, timeouts: Timeouts(context.State) + 1);

        public JsonElement? ViewFor(MatchViewContext context) => context.State;

        private static int Count(JsonElement? state) => state?.GetProperty("count").GetInt32() ?? 0;

        private static int Timeouts(JsonElement? state) => state?.GetProperty("timeouts").GetInt32() ?? 0;

        private static JsonElement With(JsonElement? state, int? count = null, int? timeouts = null) =>
            JsonSerializer.SerializeToElement(new { count = count ?? Count(state), timeouts = timeouts ?? Timeouts(state) });
    }
}
