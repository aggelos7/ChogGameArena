using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Channels;
using ChogGames.Api.Realtime;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;

namespace ChogGames.Api.Tests.Realtime;

public sealed class RoomHubTests(MigratedPostgresDatabase database) : IClassFixture<MigratedPostgresDatabase>, IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly List<IAsyncDisposable> _disposables = [];
    private ApiFactory _factory = null!;

    private sealed record RoomDto(string Code, string Status);

    public async Task InitializeAsync()
    {
        if (database.ConnectionString.Length > 0)
        {
            await using var db = database.CreateDbContext();
            await db.Database.ExecuteSqlRawAsync("TRUNCATE \"GuestSessions\", \"Players\" CASCADE");
        }

        _factory = CreateFactory();
    }

    public async Task DisposeAsync()
    {
        foreach (var disposable in _disposables)
        {
            await disposable.DisposeAsync();
        }

        await _factory.DisposeAsync();
    }

    private ApiFactory CreateFactory(int? requestsPerMinute = null)
    {
        var settings = new Dictionary<string, string?> { ["Games:Items:0:Status"] = "Available" };
        if (requestsPerMinute is { } limit)
        {
            settings["Rooms:RequestsPerMinutePerPlayer"] = limit.ToString();
        }

        return new ApiFactory(database.ConnectionString, settings);
    }

    /// <summary>A browser with a guest session; the cookie is kept so live connections can send it too.</summary>
    private sealed record Guest(HttpClient Client, string Cookie);

    private async Task<Guest> GuestAsync(string name, ApiFactory? factory = null)
    {
        var client = (factory ?? _factory).CreateClient(
            new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), HandleCookies = false });
        using var response = await client.PostAsJsonAsync("/api/guest-sessions", new { displayName = name });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var cookie = response.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
        client.DefaultRequestHeaders.Add("Cookie", cookie);
        return new Guest(client, cookie);
    }

    /// <summary>Collects the <c>RoomUpdated</c> snapshots one live connection receives.</summary>
    private sealed class Listener(HubConnection connection) : IAsyncDisposable
    {
        private readonly Channel<JsonElement> _changes = Channel.CreateUnbounded<JsonElement>();

        public HubConnection Connection => connection;

        public void Listen() => connection.On<JsonElement>(nameof(IRoomClient.RoomUpdated), room => _changes.Writer.TryWrite(room));

        /// <summary>The code of the room in the next snapshot.</summary>
        public async Task<string> NextChangeAsync() => (await NextSnapshotAsync()).GetProperty("code").GetString()!;

        public async Task<JsonElement> NextSnapshotAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            return await _changes.Reader.ReadAsync(timeout.Token);
        }

        /// <summary>Called once the same notice has reached a member, so the server has already sent it.</summary>
        public async Task AssertNothingReceivedAsync()
        {
            await Task.Delay(TimeSpan.FromMilliseconds(300));
            Assert.False(_changes.Reader.TryRead(out var room), $"Unexpected snapshot: {room}.");
        }

        /// <summary>Returns the snapshot subscribing answers with.</summary>
        public Task<JsonElement> SubscribeAsync(string code) => connection.InvokeAsync<JsonElement>(nameof(RoomHub.SubscribeRoom), code);

        public ValueTask DisposeAsync() => connection.DisposeAsync();
    }

    private HubConnection BuildConnection(string? cookie, ApiFactory? factory = null)
    {
        var server = (factory ?? _factory).Server;
        return new HubConnectionBuilder()
            .WithUrl(new Uri($"https://localhost{RoomHub.Path}"), HttpTransportType.WebSockets, options =>
            {
                options.SkipNegotiation = true;
                options.WebSocketFactory = async (context, cancellationToken) =>
                {
                    var client = server.CreateWebSocketClient();
                    if (cookie is not null)
                    {
                        client.ConfigureRequest = request => request.Headers.Cookie = cookie;
                    }

                    return await client.ConnectAsync(context.Uri, cancellationToken);
                };
            })
            .Build();
    }

    private async Task<Listener> ConnectAsync(Guest guest, ApiFactory? factory = null)
    {
        var listener = new Listener(BuildConnection(guest.Cookie, factory));
        _disposables.Add(listener);
        listener.Listen();
        await listener.Connection.StartAsync();
        return listener;
    }

    private static async Task<string> CreateRoomAsync(Guest host)
    {
        using var response = await host.Client.PostAsJsonAsync("/api/rooms", new { gameType = "chog-life" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<RoomDto>(Json))!.Code;
    }

    private static async Task SendAsync(Guest guest, HttpMethod method, string path, object? body = null, HttpStatusCode expected = HttpStatusCode.OK)
    {
        using var request = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body) };
        using var response = await guest.Client.SendAsync(request);
        Assert.Equal(expected, response.StatusCode);
    }

    private static Task JoinAsync(Guest guest, string code) => SendAsync(guest, HttpMethod.Post, $"/api/rooms/{code}/join");

    private static Task ReadyAsync(Guest guest, string code, bool ready = true) =>
        SendAsync(guest, HttpMethod.Put, $"/api/rooms/{code}/ready", new { ready });

    private static Task LeaveAsync(Guest guest, string code) =>
        SendAsync(guest, HttpMethod.Post, $"/api/rooms/{code}/leave", expected: HttpStatusCode.NoContent);

    private static async Task AssertRefusedAsync(Listener listener, string code, string expectedCode = RoomHub.ErrorCodes.RoomNotFound)
    {
        var error = await Assert.ThrowsAsync<HubException>(() => listener.SubscribeAsync(code));
        Assert.EndsWith(expectedCode, error.Message);
    }

    [PostgresFact]
    public async Task Connecting_without_a_session_is_refused()
    {
        using var anonymous = _factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        using var negotiate = await anonymous.PostAsync($"{RoomHub.Path}/negotiate?negotiateVersion=1", null);
        Assert.Equal(HttpStatusCode.Unauthorized, negotiate.StatusCode);

        await using var noCookie = BuildConnection(cookie: null);
        await Assert.ThrowsAnyAsync<Exception>(() => noCookie.StartAsync());

        await using var forged = BuildConnection("chog_session=not-a-real-session-token");
        await Assert.ThrowsAnyAsync<Exception>(() => forged.StartAsync());
    }

    [PostgresFact]
    public async Task Members_hear_about_every_change_to_their_room()
    {
        var alice = await GuestAsync("Alice");
        var bob = await GuestAsync("Bob");
        var code = await CreateRoomAsync(alice);
        var aliceLive = await ConnectAsync(alice);
        await aliceLive.SubscribeAsync(code.ToLowerInvariant());

        await JoinAsync(bob, code);
        Assert.Equal(code, await aliceLive.NextChangeAsync());

        var bobLive = await ConnectAsync(bob);
        await bobLive.SubscribeAsync(code);
        await bobLive.SubscribeAsync(code);

        await ReadyAsync(alice, code);
        Assert.Equal(code, await aliceLive.NextChangeAsync());
        Assert.Equal(code, await bobLive.NextChangeAsync());

        await SendAsync(bob, HttpMethod.Put, "/api/characters/selected", new { kind = "Standard", id = "mint" });
        Assert.Equal(code, await aliceLive.NextChangeAsync());
        Assert.Equal(code, await bobLive.NextChangeAsync());

        // Rejoining changes nothing, so nobody is told anything; subscribing twice still delivers once.
        await JoinAsync(bob, code);
        await ReadyAsync(bob, code);
        Assert.Equal(code, await aliceLive.NextChangeAsync());
        Assert.Equal(code, await bobLive.NextChangeAsync());
        await aliceLive.AssertNothingReceivedAsync();
        await bobLive.AssertNothingReceivedAsync();

        await SendAsync(alice, HttpMethod.Post, $"/api/rooms/{code}/start");
        Assert.Equal(code, await aliceLive.NextChangeAsync());
        Assert.Equal(code, await bobLive.NextChangeAsync());
    }

    [PostgresFact]
    public async Task A_non_member_cannot_subscribe_and_hears_nothing()
    {
        var alice = await GuestAsync("Alice");
        var mallory = await GuestAsync("Mallory");
        var code = await CreateRoomAsync(alice);
        var aliceLive = await ConnectAsync(alice);
        await aliceLive.SubscribeAsync(code);
        var malloryLive = await ConnectAsync(mallory);

        // Someone else's room, an unknown code and a malformed one all look the same.
        await AssertRefusedAsync(malloryLive, code);
        await AssertRefusedAsync(malloryLive, code == "ZZZZZZ" ? "YYYYYY" : "ZZZZZZ");
        await AssertRefusedAsync(malloryLive, "not-a-code");
        await AssertRefusedAsync(malloryLive, "");

        await ReadyAsync(alice, code);
        Assert.Equal(code, await aliceLive.NextChangeAsync());
        await malloryLive.AssertNothingReceivedAsync();

        // The refusals changed nothing in the room.
        using var view = await alice.Client.GetAsync($"/api/rooms/{code}");
        Assert.Single((await view.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("members").EnumerateArray());
    }

    [PostgresFact]
    public async Task The_hub_offers_no_commands_beyond_subscribing()
    {
        var alice = await GuestAsync("Alice");
        var code = await CreateRoomAsync(alice);
        var aliceLive = await ConnectAsync(alice);

        await Assert.ThrowsAsync<HubException>(() => aliceLive.Connection.InvokeAsync("StartRoom", code));
        await Assert.ThrowsAsync<HubException>(() => aliceLive.Connection.InvokeAsync("OnDisconnectedAsync", (object?)null));

        using var view = await alice.Client.GetAsync($"/api/rooms/{code}");
        Assert.Equal("Waiting", (await view.Content.ReadFromJsonAsync<RoomDto>(Json))!.Status);
    }

    [PostgresFact]
    public async Task A_member_who_leaves_stops_hearing_about_the_room_on_every_connection()
    {
        var alice = await GuestAsync("Alice");
        var bob = await GuestAsync("Bob");
        var code = await CreateRoomAsync(alice);
        await JoinAsync(bob, code);
        var aliceLive = await ConnectAsync(alice);
        await aliceLive.SubscribeAsync(code);
        var bobPhone = await ConnectAsync(bob);
        await bobPhone.SubscribeAsync(code);
        var bobLaptop = await ConnectAsync(bob);
        await bobLaptop.SubscribeAsync(code);

        // Bob is no longer a member, so his connections get nothing more of the room, not even his own leave (DEC-028).
        await LeaveAsync(bob, code);
        Assert.Equal(code, await aliceLive.NextChangeAsync());

        await ReadyAsync(alice, code);
        Assert.Equal(code, await aliceLive.NextChangeAsync());
        await bobPhone.AssertNothingReceivedAsync();
        await bobLaptop.AssertNothingReceivedAsync();

        await AssertRefusedAsync(bobPhone, code);
    }

    [PostgresFact]
    public async Task A_player_who_leaves_a_started_match_stops_hearing_about_it()
    {
        var alice = await GuestAsync("Alice");
        var bob = await GuestAsync("Bob");
        var carol = await GuestAsync("Carol");
        var code = await CreateRoomAsync(alice);
        await JoinAsync(bob, code);
        await JoinAsync(carol, code);
        foreach (var player in new[] { alice, bob, carol })
        {
            await ReadyAsync(player, code);
        }

        await SendAsync(alice, HttpMethod.Post, $"/api/rooms/{code}/start");
        var aliceLive = await ConnectAsync(alice);
        await aliceLive.SubscribeAsync(code);
        var bobLive = await ConnectAsync(bob);
        await bobLive.SubscribeAsync(code);

        await LeaveAsync(bob, code);
        Assert.Equal(code, await aliceLive.NextChangeAsync());
        Assert.Equal(code, await bobLive.NextChangeAsync());

        // Carol leaving abandons the match: Alice hears of it, Bob, who already left, does not.
        await LeaveAsync(carol, code);
        Assert.Equal(code, await aliceLive.NextChangeAsync());
        await bobLive.AssertNothingReceivedAsync();
        await AssertRefusedAsync(bobLive, code);

        using var view = await alice.Client.GetAsync($"/api/rooms/{code}");
        Assert.Equal("Abandoned", (await view.Content.ReadFromJsonAsync<RoomDto>(Json))!.Status);
    }

    [PostgresFact]
    public async Task Members_hear_when_the_host_leaves_and_cancels_the_room()
    {
        var alice = await GuestAsync("Alice");
        var bob = await GuestAsync("Bob");
        var code = await CreateRoomAsync(alice);
        await JoinAsync(bob, code);
        var bobLive = await ConnectAsync(bob);
        await bobLive.SubscribeAsync(code);

        await LeaveAsync(alice, code);

        Assert.Equal(code, await bobLive.NextChangeAsync());
        using var view = await bob.Client.GetAsync($"/api/rooms/{code}");
        Assert.Equal("Cancelled", (await view.Content.ReadFromJsonAsync<RoomDto>(Json))!.Status);
    }

    [PostgresFact]
    public async Task Unsubscribing_stops_the_notices()
    {
        var alice = await GuestAsync("Alice");
        var bob = await GuestAsync("Bob");
        var code = await CreateRoomAsync(alice);
        var aliceLive = await ConnectAsync(alice);
        await aliceLive.SubscribeAsync(code);
        var aliceOtherTab = await ConnectAsync(alice);
        await aliceOtherTab.SubscribeAsync(code);

        await aliceLive.Connection.InvokeAsync(nameof(RoomHub.UnsubscribeRoom), code);
        await aliceLive.Connection.InvokeAsync(nameof(RoomHub.UnsubscribeRoom), "not-a-code");
        await JoinAsync(bob, code);

        Assert.Equal(code, await aliceOtherTab.NextChangeAsync());
        await aliceLive.AssertNothingReceivedAsync();
    }

    [PostgresFact]
    public async Task Hub_calls_are_rate_limited_per_player()
    {
        await using var factory = CreateFactory(requestsPerMinute: 3);
        var alice = await GuestAsync("Alice", factory);
        var bob = await GuestAsync("Bob", factory);
        var code = await CreateRoomAsync(alice);
        var aliceLive = await ConnectAsync(alice, factory);
        var bobLive = await ConnectAsync(bob, factory);

        for (var call = 0; call < 3; call++)
        {
            await aliceLive.SubscribeAsync(code);
        }

        await AssertRefusedAsync(aliceLive, code, RoomHub.ErrorCodes.RateLimited);

        // Another player has a budget of their own.
        await AssertRefusedAsync(bobLive, code);

        // Closed before their server goes away.
        await aliceLive.DisposeAsync();
        await bobLive.DisposeAsync();
    }
}
