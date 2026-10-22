using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Channels;
using ChogGames.Api.ChogRoyale;
using ChogGames.Api.Matches;
using ChogGames.Api.Realtime;
using ChogGames.Core.Matches;
using ChogGames.Core.Rooms;
using ChogGames.Infrastructure.Matches;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ChogGames.Api.Tests.ChogRoyale;

/// <summary>
/// Chog Royale end to end with the real rules (STORY-071–078, DEC-082–089), version 2 since STORY-079 (DEC-090): a match starts with 100 MON and three private
/// cards each and plays five rounds to its end even with nobody acting; actions are hidden from everyone else over REST and
/// live until the round resolves, idle players HODL, and a round resolves once however often it is retried. The cards
/// move MON under the round's market event, and the finished match is ranked and counted in profiles, XP and the Ladder.
/// Guests choose from every ability and a use stays private until the reveal (STORY-080, DEC-091); a player who drops
/// comes back to their round, pending action and the deadline on the server's clock, and outsiders see nothing
/// (STORY-083, DEC-093).
/// </summary>
public sealed class ChogRoyaleMatchTests(MigratedPostgresDatabase database) : IClassFixture<MigratedPostgresDatabase>, IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Empty, it keeps seats in join order and deals Pumps (draw 0) unless a test queues other cards.</summary>
    private readonly LowDraws _random = new();

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
                ["Games:Items:2:Status"] = "Available",
                ["Rooms:RequestsPerMinutePerPlayer"] = "1000",
            },
            services =>
            {
                services.AddSingleton<IRandomSource>(_random);
                services.AddSingleton<TimeProvider>(_clock);
                services.AddChogRoyaleAbilities();
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

    /// <summary>
    /// Card and market event draws (0–99) take the queued values first, then 0: a Pump, or Calm Market; every other draw
    /// is the highest allowed, so a shuffle never swaps and seats stay in join order.
    /// </summary>
    private sealed class LowDraws : IRandomSource
    {
        private readonly Queue<int> _values = new();
        private readonly object _lock = new();

        public void Enqueue(params int[] values)
        {
            lock (_lock)
            {
                foreach (var value in values)
                {
                    _values.Enqueue(value);
                }
            }
        }

        public int Next(int minInclusive, int maxExclusive)
        {
            lock (_lock)
            {
                if (maxExclusive != 100)
                {
                    return maxExclusive - 1;
                }

                return _values.Count > 0 ? _values.Dequeue() : minInclusive;
            }
        }
    }

    private sealed record Guest(HttpClient Client, string Cookie, string Name);

    private async Task<Guest> GuestAsync(string name)
    {
        var client = _factory.CreateClient(
            new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), HandleCookies = false });
        _disposables.Add(new DisposableClient(client));
        using var response = await client.PostAsJsonAsync("/api/guest-sessions", new { displayName = name });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var cookie = response.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
        client.DefaultRequestHeaders.Add("Cookie", cookie);
        return new Guest(client, cookie, name);
    }

    private sealed class DisposableClient(HttpClient client) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            client.Dispose();
            return ValueTask.CompletedTask;
        }
    }

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

    private static async Task<JsonElement> ViewAsync(Guest guest, string code)
    {
        using var response = await guest.Client.GetAsync($"/api/rooms/{code}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>(Json);
    }

    private async Task<string> CreatedAsync(Guest host)
    {
        using var created = await host.Client.PostAsJsonAsync("/api/rooms", new { gameType = "chog-royale" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (await created.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("code").GetString()!;
    }

    /// <summary>
    /// A started match, players joining a second apart so they are seated in join order, each with Stop Loss; returns its
    /// code.
    /// </summary>
    private Task<string> StartedAsync(params Guest[] players) =>
        StartedWithAsync([.. players.Select(_ => "stop_loss")], players);

    /// <summary>A started match whose players lock the given abilities, in seat order (STORY-080).</summary>
    private async Task<string> StartedWithAsync(string[] abilities, params Guest[] players)
    {
        var code = await CreatedAsync(players[0]);
        foreach (var player in players[1..])
        {
            _clock.Advance(TimeSpan.FromSeconds(1));
            (await player.Client.PostAsync($"/api/rooms/{code}/join", null)).Dispose();
        }

        foreach (var (player, ability) in players.Zip(abilities))
        {
            using var chosen = await player.Client.PutAsJsonAsync($"/api/rooms/{code}/ability", new { abilityId = ability });
            Assert.Equal(HttpStatusCode.OK, chosen.StatusCode);
            using var ready = await player.Client.PutAsJsonAsync($"/api/rooms/{code}/ready", new { ready = true });
            Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        }

        using var started = await players[0].Client.PostAsync($"/api/rooms/{code}/start", null);
        Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        return code;
    }

    private async Task<Guest[]> GuestsAsync(int count) =>
        await Task.WhenAll(Enumerable.Range(1, count).Select(i => GuestAsync($"Player {i}")));

    private static Task<HttpResponseMessage> SubmitAsync(Guest guest, string code, object payload, Guid? actionId = null) =>
        CommandAsync(guest, code, "SUBMIT_ACTION", payload, actionId);

    private static Task<HttpResponseMessage> UseAbilityAsync(Guest guest, string code, string abilityId) =>
        CommandAsync(guest, code, "USE_ABILITY", new { abilityId });

    private static async Task<HttpResponseMessage> CommandAsync(Guest guest, string code, string type, object payload, Guid? actionId = null)
    {
        var room = await ViewAsync(guest, code);
        return await guest.Client.PostAsJsonAsync(
            $"/api/rooms/{code}/commands",
            new
            {
                actionId = actionId ?? Guid.NewGuid(),
                matchId = room.GetProperty("matchId").GetGuid(),
                expectedVersion = room.GetProperty("version").GetInt64(),
                type,
                payload,
            });
    }

    private static async Task OkAsync(Task<HttpResponseMessage> sending)
    {
        using var response = await sending;
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task RefusedAsync(Task<HttpResponseMessage> sending, string code, string? reason = null)
    {
        using var response = await sending;
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal(code, problem.GetProperty("code").GetString());
        if (reason is not null)
        {
            Assert.Equal(reason, problem.GetProperty("reason").GetString());
        }
    }

    private async Task<IReadOnlyList<TimedOutTurn>> SweepAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<TurnTimeoutService>().TimeOutOverdueTurnsAsync(CancellationToken.None);
    }

    private async Task<GameRoom> StoredAsync(string code)
    {
        await using var db = database.CreateDbContext();
        return await db.GameRooms.AsNoTracking().SingleAsync(r => r.Code == code);
    }

    private static JsonElement State(JsonElement room) => room.GetProperty("state");

    private static string[] HandIds(JsonElement room) =>
        [.. State(room).GetProperty("hand").EnumerateArray().Select(c => c.GetProperty("id").GetString()!)];

    /// <summary>A member's result: MON, change, MON lost to opponents and successful cards (DEC-089).</summary>
    private static (int Mon, int Change, int Lost, int SuccessfulCards) Result(JsonElement member)
    {
        var result = member.GetProperty("result");
        return (result.GetProperty("mon").GetInt32(), result.GetProperty("change").GetInt32(), result.GetProperty("lost").GetInt32(), result.GetProperty("successfulCards").GetInt32());
    }

    /// <summary>The first round opens 5 s after the start.</summary>
    private void ToFirstRound() => _clock.Advance(TimeSpan.FromSeconds(5));

    [PostgresFact]
    public async Task A_room_of_one_cannot_start()
    {
        var alice = await GuestAsync("Alice");
        var code = await CreatedAsync(alice);
        (await alice.Client.PutAsJsonAsync($"/api/rooms/{code}/ready", new { ready = true })).Dispose();

        using var started = await alice.Client.PostAsync($"/api/rooms/{code}/start", null);

        Assert.Equal(HttpStatusCode.Conflict, started.StatusCode);
        Assert.Equal("not_enough_players", (await started.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("code").GetString());
        Assert.Equal(RoomStatus.Waiting, (await StoredAsync(code)).Status);
    }

    [PostgresTheory]
    [InlineData(2)]
    [InlineData(6)]
    public async Task A_match_starts_with_100_mon_and_reaches_its_end_after_exactly_five_rounds_without_client_input(int count)
    {
        var players = await GuestsAsync(count);
        var code = await StartedAsync(players);

        var first = await ViewAsync(players[0], code);
        Assert.Equal(("Rounds", 1), (first.GetProperty("play").GetString(), first.GetProperty("turnNumber").GetInt32()));
        Assert.Equal((2, 1, 5), (State(first).GetProperty("rulesVersion").GetInt32(), State(first).GetProperty("round").GetInt32(), State(first).GetProperty("rounds").GetInt32()));
        Assert.All(State(first).GetProperty("players").EnumerateArray(), p => Assert.Equal((100, 3), (p.GetProperty("mon").GetInt32(), p.GetProperty("cards").GetInt32())));
        Assert.Equal(3, HandIds(first).Length);

        // 5 s announcement, then each round: 25 s to decide, resolved by the sweep, 20 s to the next one (version 2).
        ToFirstRound();
        for (var round = 1; round <= 5; round++)
        {
            Assert.Equal(RoomStatus.Active, (await StoredAsync(code)).Status);
            _clock.Advance(TimeSpan.FromSeconds(25));
            Assert.Single(await SweepAsync());
            _clock.Advance(TimeSpan.FromSeconds(20));
        }

        var stored = await StoredAsync(code);
        Assert.Equal(RoomStatus.Completed, stored.Status);
        Assert.Equal(5, stored.TurnNumber);
        Assert.All(stored.Members, m => Assert.Equal((1, 5), (m.Rank!.Value, m.MissedTurns)));
        var final = await ViewAsync(players[^1], code);
        Assert.Equal(5, State(final).GetProperty("ledger").GetArrayLength());
        Assert.All(final.GetProperty("members").EnumerateArray(), m => Assert.Equal(100, m.GetProperty("result").GetProperty("mon").GetInt32()));
        Assert.Empty(await SweepAsync());
    }

    [PostgresFact]
    public async Task Actions_and_hands_stay_hidden_from_the_others_over_rest_and_live_until_the_reveal()
    {
        // Alice is dealt Pump, Rug Pull and Diamond Hands; Bob three Pumps.
        _random.Enqueue(0, 30, 50, 0, 0, 0);
        var (alice, bob) = (await GuestAsync("Alice"), await GuestAsync("Bob"));
        var code = await StartedAsync(alice, bob);
        var bobLive = await ConnectAsync(bob, code);
        ToFirstRound();
        var aliceHand = HandIds(await ViewAsync(alice, code));

        await OkAsync(SubmitAsync(alice, code, new { card = aliceHand[1], target = 2, @lock = true }));

        var pushed = await bobLive.NextAsync();
        foreach (var seen in new[] { pushed, await ViewAsync(bob, code) })
        {
            Assert.False(State(seen).TryGetProperty("action", out _));
            Assert.All(aliceHand, id => Assert.DoesNotContain($"\"{id}\"", seen.GetRawText()));
            var aliceSeen = seen.GetProperty("members").EnumerateArray().Single(m => m.GetProperty("displayName").GetString() == "Alice");
            Assert.True(aliceSeen.GetProperty("hasSubmitted").GetBoolean() && aliceSeen.GetProperty("hasLocked").GetBoolean());
        }

        var mine = State(await ViewAsync(alice, code)).GetProperty("action");
        Assert.Equal((aliceHand[1], 2), (mine.GetProperty("card").GetString(), mine.GetProperty("target").GetInt32()));

        // Bob's lock resolves the round at once: the ledger reveals Alice's Rug Pull to everyone.
        await OkAsync(SubmitAsync(bob, code, new { hodl = true, @lock = true }));
        var revealed = State(await ViewAsync(bob, code)).GetProperty("ledger")[0].GetProperty("entries");
        Assert.Equal(("RugPull", 2), (revealed[0].GetProperty("card").GetString(), revealed[0].GetProperty("target").GetInt32()));
        Assert.False(revealed[1].TryGetProperty("card", out _));
    }

    [PostgresFact]
    public async Task Only_the_last_submission_counts_and_an_idle_player_hodls_at_the_deadline()
    {
        var (alice, bob) = (await GuestAsync("Alice"), await GuestAsync("Bob"));
        var code = await StartedAsync(alice, bob);
        ToFirstRound();
        var hand = HandIds(await ViewAsync(alice, code));

        await OkAsync(SubmitAsync(alice, code, new { card = hand[0] }));
        await OkAsync(SubmitAsync(alice, code, new { hodl = true }));
        await OkAsync(SubmitAsync(alice, code, new { card = hand[2] }));
        _clock.Advance(TimeSpan.FromSeconds(25));
        Assert.Single(await SweepAsync());

        var after = await ViewAsync(alice, code);
        var entries = State(after).GetProperty("ledger")[0].GetProperty("entries");
        Assert.Equal(("Pump", false), (entries[0].GetProperty("card").GetString(), entries[0].GetProperty("byDefault").GetBoolean()));
        Assert.True(entries[1].GetProperty("byDefault").GetBoolean());
        Assert.Equal([hand[0], hand[1], "c7"], HandIds(after));
        var stored = await StoredAsync(code);
        Assert.Equal([0, 1], stored.Members.OrderBy(m => m.Seat).Select(m => m.MissedTurns));
    }

    [PostgresFact]
    public async Task Bad_cards_targets_and_late_or_duplicate_actions_are_refused_without_a_change()
    {
        // Alice holds Pump, Rug Pull, Diamond Hands.
        _random.Enqueue(0, 30, 50);
        var (alice, bob, carol) = (await GuestAsync("Alice"), await GuestAsync("Bob"), await GuestAsync("Carol"));
        var code = await StartedAsync(alice, bob, carol);
        (await carol.Client.PostAsync($"/api/rooms/{code}/leave", null)).Dispose();
        ToFirstRound();
        var hand = HandIds(await ViewAsync(alice, code));
        var bobHand = HandIds(await ViewAsync(bob, code));
        var version = (await StoredAsync(code)).Version;

        await RefusedAsync(SubmitAsync(alice, code, new { card = bobHand[0] }), "command_rejected", "card_not_in_hand");
        await RefusedAsync(SubmitAsync(alice, code, new { card = hand[1] }), "command_rejected", "target_required");
        await RefusedAsync(SubmitAsync(alice, code, new { card = hand[1], target = 1 }), "command_rejected", "invalid_target");
        await RefusedAsync(SubmitAsync(alice, code, new { card = hand[1], target = 3 }), "command_rejected", "invalid_target");
        await RefusedAsync(SubmitAsync(alice, code, new { hodl = true, card = hand[0] }), "command_rejected", "invalid_action");
        Assert.Equal(version, (await StoredAsync(code)).Version);

        var action = Guid.NewGuid();
        await OkAsync(SubmitAsync(alice, code, new { card = hand[1], target = 2, @lock = true }, action));
        var locked = (await StoredAsync(code)).Version;
        using (var replay = await SubmitAsync(alice, code, new { card = hand[1], target = 2, @lock = true }, action))
        {
            Assert.True((await replay.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("replayed").GetBoolean());
        }

        await RefusedAsync(SubmitAsync(alice, code, new { hodl = true }), "action_locked");
        _clock.Advance(TimeSpan.FromSeconds(25));
        await RefusedAsync(SubmitAsync(bob, code, new { hodl = true }), "round_closed");
        Assert.Equal(locked, (await StoredAsync(code)).Version);
    }

    [PostgresFact]
    public async Task A_round_resolves_once_however_often_its_last_lock_or_the_sweep_is_retried()
    {
        var (alice, bob) = (await GuestAsync("Alice"), await GuestAsync("Bob"));
        var code = await StartedAsync(alice, bob);
        ToFirstRound();
        var hand = HandIds(await ViewAsync(alice, code));
        await OkAsync(SubmitAsync(alice, code, new { card = hand[0], @lock = true }));
        var last = Guid.NewGuid();

        await OkAsync(SubmitAsync(bob, code, new { hodl = true, @lock = true }, last));
        var resolved = await StoredAsync(code);
        using (var retry = await SubmitAsync(bob, code, new { hodl = true, @lock = true }, last))
        {
            Assert.True((await retry.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("replayed").GetBoolean());
        }

        _clock.Advance(TimeSpan.FromSeconds(25));
        Assert.Empty(await SweepAsync());

        var stored = await StoredAsync(code);
        Assert.Equal((2, resolved.Version, resolved.State), (stored.TurnNumber, stored.Version, stored.State));
        Assert.Equal(1, State(await ViewAsync(alice, code)).GetProperty("ledger").GetArrayLength());
    }

    [PostgresFact]
    public async Task Leaving_mid_match_keeps_the_others_playing_and_hides_the_leavers_hand()
    {
        var (alice, bob, carol) = (await GuestAsync("Alice"), await GuestAsync("Bob"), await GuestAsync("Carol"));
        var code = await StartedAsync(alice, bob, carol);
        ToFirstRound();
        await OkAsync(SubmitAsync(alice, code, new { hodl = true, @lock = true }));
        await OkAsync(SubmitAsync(bob, code, new { hodl = true, @lock = true }));

        (await carol.Client.PostAsync($"/api/rooms/{code}/leave", null)).Dispose();
        Assert.Single(await SweepAsync());

        var seen = await ViewAsync(carol, code);
        Assert.Empty(State(seen).GetProperty("hand").EnumerateArray());
        Assert.Equal([1, 2], State(seen).GetProperty("players").EnumerateArray().Select(p => p.GetProperty("seat").GetInt32()));
        Assert.Equal(2, (await StoredAsync(code)).TurnNumber);
    }

    [PostgresFact]
    public async Task Every_player_sees_the_rounds_event_before_deciding_and_a_pump_pays_30_under_meme_hype()
    {
        // Six Pumps dealt, then round 1's event: a draw of 30 is Meme Hype.
        _random.Enqueue(0, 0, 0, 0, 0, 0, 30);
        var (alice, bob) = (await GuestAsync("Alice"), await GuestAsync("Bob"));
        var code = await StartedAsync(alice, bob);

        foreach (var player in new[] { alice, bob })
        {
            Assert.Equal("MemeHype", State(await ViewAsync(player, code)).GetProperty("event").GetString());
        }

        ToFirstRound();
        await OkAsync(SubmitAsync(alice, code, new { card = HandIds(await ViewAsync(alice, code))[0], @lock = true }));
        await OkAsync(SubmitAsync(bob, code, new { hodl = true, @lock = true }));

        var round = State(await ViewAsync(bob, code)).GetProperty("ledger")[0];
        Assert.Equal("MemeHype", round.GetProperty("event").GetString());
        var pump = round.GetProperty("entries")[0];
        Assert.Equal(("Applied", 30, 130), (pump.GetProperty("outcome").GetString(), pump.GetProperty("amount").GetInt32(), pump.GetProperty("balance").GetInt32()));
    }

    [PostgresFact]
    public async Task A_finished_match_is_ranked_counted_in_profiles_xp_and_the_ladder_and_refuses_later_actions()
    {
        var (alice, bob) = (await GuestAsync("Alice"), await GuestAsync("Bob"));
        var code = await StartedAsync(alice, bob);

        // Alice pumps every round (she is only dealt Pumps) and Bob HODLs; both lock, so each round resolves at once.
        ToFirstRound();
        for (var round = 1; round <= 5; round++)
        {
            await OkAsync(SubmitAsync(alice, code, new { card = HandIds(await ViewAsync(alice, code))[0], @lock = true }));
            await OkAsync(SubmitAsync(bob, code, new { hodl = true, @lock = true }));
            _clock.Advance(TimeSpan.FromSeconds(20));
        }

        var final = await ViewAsync(bob, code);
        Assert.Equal("Completed", final.GetProperty("status").GetString());
        var members = final.GetProperty("members").EnumerateArray().ToDictionary(m => m.GetProperty("displayName").GetString()!);
        Assert.Equal((1, 2), (members["Alice"].GetProperty("rank").GetInt32(), members["Bob"].GetProperty("rank").GetInt32()));
        Assert.Equal((200, 100, 0, 5), Result(members["Alice"]));
        Assert.Equal((100, 0, 0, 0), Result(members["Bob"]));

        var profiles = new Dictionary<string, JsonElement>();
        foreach (var player in new[] { alice, bob })
        {
            using var response = await player.Client.GetAsync("/api/profile");
            profiles[player.Name] = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        }

        Assert.Equal((1, 1), (profiles["Alice"].GetProperty("gamesPlayed").GetInt32(), profiles["Alice"].GetProperty("wins").GetInt32()));
        Assert.Equal((1, 0), (profiles["Bob"].GetProperty("gamesPlayed").GetInt32(), profiles["Bob"].GetProperty("wins").GetInt32()));
        Assert.True(profiles["Alice"].GetProperty("xp").GetInt32() > profiles["Bob"].GetProperty("xp").GetInt32());
        Assert.True(profiles["Bob"].GetProperty("xp").GetInt32() > 0);

        using (var ladder = await alice.Client.GetAsync("/api/leaderboards?game=chog-royale"))
        {
            Assert.Equal(HttpStatusCode.OK, ladder.StatusCode);
            var entries = (await ladder.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("entries");
            Assert.Equal(
                [("Alice", profiles["Alice"].GetProperty("xp").GetInt32(), 1), ("Bob", profiles["Bob"].GetProperty("xp").GetInt32(), 0)],
                entries.EnumerateArray().Select(e => (e.GetProperty("displayName").GetString()!, e.GetProperty("xp").GetInt32(), e.GetProperty("wins").GetInt32())));
        }

        var version = (await StoredAsync(code)).Version;
        await RefusedAsync(SubmitAsync(alice, code, new { hodl = true }), "room_closed");
        Assert.Equal(version, (await StoredAsync(code)).Version);
    }

    // STORY-080: abilities.

    /// <summary>A member's ability as the snapshot shows it.</summary>
    private static JsonElement AbilityOf(JsonElement room, string displayName) =>
        room.GetProperty("members").EnumerateArray().Single(m => m.GetProperty("displayName").GetString() == displayName).GetProperty("ability");

    [PostgresFact]
    public async Task Guests_choose_from_all_four_abilities_and_a_use_stays_private_until_the_reveal()
    {
        // Six Pumps dealt, then round 1's event Calm Market (0), and Alpha Leak's early draw for round 2: Meme Hype (30).
        _random.Enqueue(0, 0, 0, 0, 0, 0, 0, 30);
        var (alice, bob) = (await GuestAsync("Alice"), await GuestAsync("Bob"));
        var code = await CreatedAsync(alice);
        (await bob.Client.PostAsync($"/api/rooms/{code}/join", null)).Dispose();
        foreach (var guest in new[] { alice, bob })
        {
            var offered = (await ViewAsync(guest, code)).GetProperty("abilities").EnumerateArray().Select(a => a.GetProperty("id").GetString()!).ToArray();
            Assert.Equal(["diamond_hands_plus", "alpha_leak", "double_pump", "stop_loss"], offered);
        }

        foreach (var (guest, ability) in new[] { (alice, "alpha_leak"), (bob, "stop_loss") })
        {
            (await guest.Client.PutAsJsonAsync($"/api/rooms/{code}/ability", new { abilityId = ability })).Dispose();
            (await guest.Client.PutAsJsonAsync($"/api/rooms/{code}/ready", new { ready = true })).Dispose();
        }

        (await alice.Client.PostAsync($"/api/rooms/{code}/start", null)).Dispose();
        await RefusedAsync(UseAbilityAsync(alice, code, "alpha_leak"), "round_not_open");
        ToFirstRound();

        await OkAsync(UseAbilityAsync(alice, code, "alpha_leak"));

        var mine = await ViewAsync(alice, code);
        Assert.Equal(("MemeHype", "alpha_leak", 0), (State(mine).GetProperty("nextEvent").GetString(), State(mine).GetProperty("ability").GetString(), AbilityOf(mine, "Alice").GetProperty("usesLeft").GetInt32()));
        var theirs = await ViewAsync(bob, code);
        Assert.False(State(theirs).TryGetProperty("nextEvent", out _));
        Assert.False(State(theirs).TryGetProperty("ability", out _));
        Assert.Equal(1, AbilityOf(theirs, "Alice").GetProperty("usesLeft").GetInt32());
        await RefusedAsync(UseAbilityAsync(alice, code, "alpha_leak"), "command_rejected", "ability_used_up");

        // The reveal shows the use to everyone, and round 2 plays the leaked event.
        _clock.Advance(TimeSpan.FromSeconds(25));
        Assert.Single(await SweepAsync());
        var revealed = await ViewAsync(bob, code);
        Assert.Equal(0, AbilityOf(revealed, "Alice").GetProperty("usesLeft").GetInt32());
        Assert.Equal("alpha_leak", State(revealed).GetProperty("ledger")[0].GetProperty("entries")[0].GetProperty("ability").GetString());
        Assert.Equal("MemeHype", State(revealed).GetProperty("event").GetString());
    }

    // STORY-083: reconnect and AFK.

    [PostgresFact]
    public async Task A_player_who_drops_mid_decision_returns_to_their_pending_action_and_the_real_remaining_time()
    {
        var (alice, bob) = (await GuestAsync("Alice"), await GuestAsync("Bob"));
        var code = await StartedAsync(alice, bob);
        var aliceLive = await ConnectAsync(alice, code);
        ToFirstRound();
        var hand = HandIds(await ViewAsync(alice, code));
        await OkAsync(SubmitAsync(alice, code, new { card = hand[0] }));
        await OkAsync(UseAbilityAsync(alice, code, "stop_loss"));
        await aliceLive.DisposeAsync();

        // Six seconds later she comes back on a new connection, with only her cookie.
        _clock.Advance(TimeSpan.FromSeconds(6));
        var returned = await GuestBackAsync(alice);
        using (var current = await returned.Client.GetAsync("/api/rooms/current"))
        {
            Assert.Equal(code, (await current.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("code").GetString());
        }

        var back = await ConnectAsync(returned, code);
        var snapshot = await back.SubscribeAsync(code);
        foreach (var seen in new[] { snapshot, await ViewAsync(returned, code) })
        {
            Assert.Equal((1, "Rounds"), (State(seen).GetProperty("round").GetInt32(), seen.GetProperty("play").GetString()));
            Assert.Equal(hand[0], State(seen).GetProperty("action").GetProperty("card").GetString());
            Assert.Equal("stop_loss", State(seen).GetProperty("ability").GetString());
            Assert.Equal(hand, HandIds(seen));
            var left = seen.GetProperty("turnEndsAt").GetDateTimeOffset() - seen.GetProperty("serverTime").GetDateTimeOffset();
            Assert.Equal(TimeSpan.FromSeconds(19), left);
            var bobSeen = seen.GetProperty("members").EnumerateArray().Single(m => m.GetProperty("displayName").GetString() == "Bob");
            Assert.False(bobSeen.GetProperty("hasSubmitted").GetBoolean());
        }

        // Bob, who never acted, sees none of it.
        var bobs = await ViewAsync(bob, code);
        Assert.False(State(bobs).TryGetProperty("action", out _));
        Assert.DoesNotContain($"\"{hand[0]}\"", bobs.GetRawText(), StringComparison.Ordinal);
    }

    [PostgresFact]
    public async Task A_player_who_returns_after_the_deadline_sees_the_rounds_result_and_afk_players_hodl_every_round()
    {
        var (alice, bob) = (await GuestAsync("Alice"), await GuestAsync("Bob"));
        var code = await StartedAsync(alice, bob);
        ToFirstRound();
        var hand = HandIds(await ViewAsync(alice, code));
        await OkAsync(SubmitAsync(alice, code, new { card = hand[0] }));

        // Alice drops; the round resolves at its deadline without her, and Bob stays AFK for two rounds.
        _clock.Advance(TimeSpan.FromSeconds(25));
        Assert.Single(await SweepAsync());
        _clock.Advance(TimeSpan.FromSeconds(20 + 25));
        Assert.Single(await SweepAsync());

        var back = await ViewAsync(await GuestBackAsync(alice), code);
        Assert.Equal(3, State(back).GetProperty("round").GetInt32());
        Assert.False(State(back).TryGetProperty("action", out _));
        var ledger = State(back).GetProperty("ledger");
        Assert.Equal(("Pump", 120), (ledger[0].GetProperty("entries")[0].GetProperty("card").GetString(), ledger[0].GetProperty("entries")[0].GetProperty("balance").GetInt32()));
        Assert.All(ledger.EnumerateArray(), round => Assert.True(round.GetProperty("entries")[1].GetProperty("byDefault").GetBoolean()));
        Assert.True(ledger[1].GetProperty("entries")[0].GetProperty("byDefault").GetBoolean());

        // Missed rounds never forfeit (DEC-081): both still play.
        var stored = await StoredAsync(code);
        Assert.Equal(RoomStatus.Active, stored.Status);
        Assert.Equal([1, 2], stored.Members.OrderBy(m => m.Seat).Select(m => m.MissedTurns));
        Assert.All(stored.Members, m => Assert.False(m.HasLeft));
    }

    [PostgresFact]
    public async Task Outsiders_get_no_match_state_over_rest_or_live()
    {
        var (alice, bob, mallory) = (await GuestAsync("Alice"), await GuestAsync("Bob"), await GuestAsync("Mallory"));
        var code = await StartedAsync(alice, bob);
        ToFirstRound();

        using (var response = await mallory.Client.GetAsync($"/api/rooms/{code}"))
        {
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("room_not_found", (await response.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("code").GetString());
        }

        var server = _factory.Server;
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri($"https://localhost{RoomHub.Path}"), HttpTransportType.WebSockets, options =>
            {
                options.SkipNegotiation = true;
                options.WebSocketFactory = async (context, cancellationToken) =>
                {
                    var client = server.CreateWebSocketClient();
                    client.ConfigureRequest = request => request.Headers.Cookie = mallory.Cookie;
                    return await client.ConnectAsync(context.Uri, cancellationToken);
                };
            })
            .Build();
        var malloryLive = new Listener(connection);
        _disposables.Add(malloryLive);
        await malloryLive.StartAsync();
        await Assert.ThrowsAsync<HubException>(() => malloryLive.SubscribeAsync(code));
        using (var current = await mallory.Client.GetAsync("/api/rooms/current"))
        {
            Assert.NotEqual(HttpStatusCode.OK, current.StatusCode);
        }
    }

    /// <summary>The same guest in a new browser: a new client with only their cookie.</summary>
    private Task<Guest> GuestBackAsync(Guest guest)
    {
        var client = _factory.CreateClient(
            new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), HandleCookies = false });
        _disposables.Add(new DisposableClient(client));
        client.DefaultRequestHeaders.Add("Cookie", guest.Cookie);
        return Task.FromResult(guest with { Client = client });
    }
}
