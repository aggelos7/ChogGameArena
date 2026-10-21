using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Channels;
using ChogGames.Api.Matches;
using ChogGames.Api.Realtime;
using ChogGames.Core.Matches;
using ChogGames.Core.Rooms;
using ChogGames.Infrastructure.Matches;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace ChogGames.Api.Tests.Matches;

/// <summary>
/// Simultaneous rounds in the shared engine (STORY-070, DEC-081), played by a stand-in game: every player picks a secret
/// number in the same round, revisable until they lock, and the round adds everyone's pick to their total, zero for
/// anyone who picked nothing. Others see only that a player submitted, over REST and live.
/// </summary>
public sealed class MatchRoundTests(MigratedPostgresDatabase database) : IClassFixture<MigratedPostgresDatabase>, IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static readonly MatchRoundTiming Timing = new(
        Decision: TimeSpan.FromSeconds(15), FirstOpensAfter: TimeSpan.FromSeconds(5), OpensAfter: TimeSpan.FromSeconds(20));

    private readonly List<IAsyncDisposable> _disposables = [];

    // Whole seconds, so times read back from Postgres (microseconds) compare equal.
    private readonly ManualTimeProvider _clock = new(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));

    private ApiFactory _factory = null!;

    public async Task InitializeAsync()
    {
        if (database.ConnectionString.Length > 0)
        {
            await using var db = database.CreateDbContext();
            await db.Database.ExecuteSqlRawAsync("TRUNCATE \"GuestSessions\", \"Players\" CASCADE");
        }

        // Chog Life plays the round game below; the tests resolve due rounds themselves.
        _factory = new ApiFactory(
            database.ConnectionString,
            new Dictionary<string, string?>
            {
                ["Games:Items:0:Status"] = "Available",
                ["Games:Items:1:Status"] = "Available",
                ["Rooms:RequestsPerMinutePerPlayer"] = "1000",
            },
            services =>
            {
                services.AddSingleton<TimeProvider>(_clock);
                services.RemoveAll<IMatchRules>().AddSingleton<IMatchRules, SecretRounds>();
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

    private sealed record ProblemDto(int Status, string? Code);

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

    private async Task<Guest[]> GuestsAsync(int count) =>
        await Task.WhenAll(Enumerable.Range(1, count).Select(i => GuestAsync($"Player {i}")));

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

    /// <summary>A started match, players joining a second apart so they are seated in join order; returns its first snapshot.</summary>
    private async Task<JsonElement> StartedAsync(params Guest[] players)
    {
        using var created = await players[0].Client.PostAsJsonAsync("/api/rooms", new { gameType = "chog-life" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var code = (await created.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("code").GetString()!;
        foreach (var player in players[1..])
        {
            _clock.Advance(TimeSpan.FromSeconds(1));
            (await player.Client.PostAsync($"/api/rooms/{code}/join", null)).Dispose();
        }

        foreach (var player in players)
        {
            using var ready = await player.Client.PutAsJsonAsync($"/api/rooms/{code}/ready", new { ready = true });
            Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        }

        using var started = await players[0].Client.PostAsync($"/api/rooms/{code}/start", null);
        Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        return await ViewAsync(players[0], code);
    }

    private static string CodeOf(JsonElement room) => room.GetProperty("code").GetString()!;

    private static Task<HttpResponseMessage> PickAsync(
        Guest guest, JsonElement room, int n, bool locks = false, long? version = null, Guid? actionId = null, string type = "PICK") =>
        guest.Client.PostAsJsonAsync(
            $"/api/rooms/{CodeOf(room)}/commands",
            new
            {
                actionId = actionId ?? Guid.NewGuid(),
                matchId = room.GetProperty("matchId").GetGuid(),
                expectedVersion = version ?? room.GetProperty("version").GetInt64(),
                type,
                payload = new { n, @lock = locks },
            });

    private static Task OkAsync(HttpResponseMessage response)
    {
        using (response)
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        return Task.CompletedTask;
    }

    private static async Task ProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        using (response)
        {
            Assert.Equal(status, response.StatusCode);
            var problem = (await response.Content.ReadFromJsonAsync<ProblemDto>(Json))!;
            Assert.Equal(((int)status, code), (problem.Status, problem.Code));
        }
    }

    private async Task<IReadOnlyList<TimedOutTurn>> SweepAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<TurnTimeoutService>().TimeOutOverdueTurnsAsync(CancellationToken.None);
    }

    private async Task<GameRoom> StoredAsync(JsonElement room)
    {
        await using var db = database.CreateDbContext();
        var id = room.GetProperty("matchId").GetGuid();
        return await db.GameRooms.AsNoTracking().SingleAsync(r => r.Id == id);
    }

    private static JsonElement State(JsonElement room) => room.GetProperty("state");

    private static int[] Totals(JsonElement room) => [.. State(room).GetProperty("totals").EnumerateArray().Select(t => t.GetInt32())];

    private void ToOpen() => _clock.Advance(Timing.FirstOpensAfter);

    [PostgresFact]
    public async Task The_room_shows_rounds_with_no_turn_and_when_the_round_opens_and_closes()
    {
        var players = await GuestsAsync(2);
        var start = _clock.Now;

        var room = await StartedAsync(players);

        var opens = start.AddSeconds(1) + Timing.FirstOpensAfter;
        Assert.Equal("Rounds", room.GetProperty("play").GetString());
        Assert.Equal((1, opens, opens + Timing.Decision, 15), (
            room.GetProperty("turnNumber").GetInt32(),
            room.GetProperty("roundOpensAt").GetDateTimeOffset(),
            room.GetProperty("turnEndsAt").GetDateTimeOffset(),
            room.GetProperty("turnSeconds").GetInt32()));
        Assert.All(room.GetProperty("members").EnumerateArray(), m => Assert.False(
            m.GetProperty("isTurn").GetBoolean() || m.GetProperty("hasSubmitted").GetBoolean() || m.GetProperty("hasLocked").GetBoolean()));
    }

    [PostgresTheory]
    [InlineData(2)]
    [InlineData(6)]
    public async Task Every_player_submits_in_the_same_round_and_the_last_lock_resolves_it_once(int count)
    {
        var players = await GuestsAsync(count);
        var room = await StartedAsync(players);
        ToOpen();

        // All built on the round's first version, sent at once: none is out of turn or stale.
        await Task.WhenAll(players.Select((p, i) => PickAsync(p, room, i + 1)).Select(async r => await OkAsync(await r)));
        await Task.WhenAll(players[..^1].Select((p, i) => PickAsync(p, room, 10 * (i + 1), locks: true)).Select(async r => await OkAsync(await r)));
        var beforeLast = await StoredAsync(room);
        Assert.Equal(1, beforeLast.TurnNumber);

        await OkAsync(await PickAsync(players[^1], room, 7, locks: true));

        var after = await ViewAsync(players[0], CodeOf(room));
        int[] expected = [.. Enumerable.Range(1, count - 1).Select(i => 10 * i), 7];
        Assert.Equal((2, 1), (after.GetProperty("turnNumber").GetInt32(), State(after).GetProperty("resolutions").GetInt32()));
        Assert.Equal(expected, Totals(after));
        Assert.Empty(await SweepAsync());
        Assert.Equal(1, State(await ViewAsync(players[0], CodeOf(room))).GetProperty("resolutions").GetInt32());
    }

    [PostgresFact]
    public async Task Others_see_only_that_someone_submitted_over_rest_and_live()
    {
        var (alice, bob) = (await GuestAsync("Alice"), await GuestAsync("Bob"));
        var room = await StartedAsync(alice, bob);
        var bobLive = await ConnectAsync(bob, CodeOf(room));
        ToOpen();

        await OkAsync(await PickAsync(alice, room, 42, locks: true));

        var pushed = await bobLive.NextAsync();
        foreach (var seen in new[] { pushed, await ViewAsync(bob, CodeOf(room)) })
        {
            var aliceSeen = seen.GetProperty("members").EnumerateArray().Single(m => m.GetProperty("displayName").GetString() == "Alice");
            Assert.True(aliceSeen.GetProperty("hasSubmitted").GetBoolean() && aliceSeen.GetProperty("hasLocked").GetBoolean());
            Assert.Equal(["mine", "resolutions", "totals"], State(seen).EnumerateObject().Select(p => p.Name).Order());
            Assert.Equal(JsonValueKind.Null, State(seen).GetProperty("mine").ValueKind);
        }

        Assert.Equal(42, State(await ViewAsync(alice, CodeOf(room))).GetProperty("mine").GetInt32());
    }

    [PostgresFact]
    public async Task Only_the_last_submission_counts()
    {
        var (alice, bob) = (await GuestAsync("Alice"), await GuestAsync("Bob"));
        var room = await StartedAsync(alice, bob);
        ToOpen();

        await OkAsync(await PickAsync(alice, room, 5));
        await OkAsync(await PickAsync(alice, room, 9));
        await OkAsync(await PickAsync(alice, room, 3, locks: true));
        await OkAsync(await PickAsync(bob, room, 1, locks: true));

        var totals = Totals(await ViewAsync(alice, CodeOf(room)));
        Assert.Equal([3, 1], totals);
    }

    [PostgresFact]
    public async Task At_the_deadline_the_round_resolves_once_with_the_default_for_idle_players_connected_or_not()
    {
        var (alice, bob, carol) = (await GuestAsync("Alice"), await GuestAsync("Bob"), await GuestAsync("Carol"));
        var room = await StartedAsync(alice, bob, carol);
        ToOpen();
        await OkAsync(await PickAsync(alice, room, 4, locks: true));
        await OkAsync(await PickAsync(bob, room, 6));

        _clock.Advance(Timing.Decision - TimeSpan.FromSeconds(1));
        Assert.Empty(await SweepAsync());
        _clock.Advance(TimeSpan.FromSeconds(1));
        var swept = await SweepAsync();
        Assert.Empty(await SweepAsync());

        Assert.Equal((CodeOf(room), (Guid?)null), (swept.Single().Code, swept.Single().ForfeitedPlayerId));
        var after = await ViewAsync(alice, CodeOf(room));
        Assert.Equal([4, 6, 0], Totals(after));
        Assert.Equal((2, 1), (after.GetProperty("turnNumber").GetInt32(), State(after).GetProperty("resolutions").GetInt32()));
        Assert.Equal(_clock.Now + Timing.OpensAfter, after.GetProperty("roundOpensAt").GetDateTimeOffset());
        var stored = await StoredAsync(room);
        Assert.Equal([0, 0, 1], stored.Members.OrderBy(m => m.Seat).Select(m => m.MissedTurns));
    }

    [PostgresFact]
    public async Task Missed_rounds_never_forfeit_and_the_last_round_completes_the_match()
    {
        var (alice, bob) = (await GuestAsync("Alice"), await GuestAsync("Bob"));
        var room = await StartedAsync(alice, bob);
        ToOpen();
        await OkAsync(await PickAsync(alice, room, 2, locks: true));

        for (var round = 1; round <= SecretRounds.Rounds; round++)
        {
            _clock.Advance(Timing.Decision);
            Assert.Single(await SweepAsync());
            _clock.Advance(Timing.OpensAfter);
        }

        var stored = await StoredAsync(room);
        Assert.Equal(RoomStatus.Completed, stored.Status);
        Assert.Equal([(1, false), (2, false)], stored.Members.OrderBy(m => m.Seat).Select(m => (m.Rank!.Value, m.HasLeft)));
        await using var db = database.CreateDbContext();
        var ids = stored.Members.Select(m => m.PlayerId).ToList();
        var played = await db.Players.AsNoTracking().Where(p => ids.Contains(p.Id)).Select(p => p.GamesPlayed).ToListAsync();
        Assert.Equal([1, 1], played);
    }

    [PostgresFact]
    public async Task Outsiders_early_late_replayed_locked_and_old_round_commands_change_nothing()
    {
        var (alice, bob, eve) = (await GuestAsync("Alice"), await GuestAsync("Bob"), await GuestAsync("Eve"));
        var room = await StartedAsync(alice, bob);
        var firstVersion = room.GetProperty("version").GetInt64();

        await ProblemAsync(await PickAsync(alice, room, 1), HttpStatusCode.Conflict, "round_not_open");
        await ProblemAsync(await PickAsync(eve, room, 1), HttpStatusCode.NotFound, "room_not_found");
        ToOpen();
        var action = Guid.NewGuid();
        await OkAsync(await PickAsync(alice, room, 1, locks: true, actionId: action));
        var version = (await StoredAsync(room)).Version;

        await OkAsync(await PickAsync(alice, room, 1, locks: true, actionId: action));
        await ProblemAsync(await PickAsync(alice, room, 8), HttpStatusCode.Conflict, "action_locked");
        await ProblemAsync(await PickAsync(bob, room, 1, type: "NOPE"), HttpStatusCode.BadRequest, "unknown_command");
        await ProblemAsync(await PickAsync(bob, room, -1), HttpStatusCode.Conflict, "command_rejected");
        Assert.Equal(version, (await StoredAsync(room)).Version);

        _clock.Advance(Timing.Decision);
        await ProblemAsync(await PickAsync(bob, room, 1), HttpStatusCode.Conflict, "round_closed");
        Assert.Equal(version, (await StoredAsync(room)).Version);

        Assert.Single(await SweepAsync());
        _clock.Advance(Timing.OpensAfter);
        await ProblemAsync(await PickAsync(bob, room, 1, version: firstVersion), HttpStatusCode.Conflict, "stale_version");
        var stored = await StoredAsync(room);
        var totals = Totals(await ViewAsync(alice, CodeOf(room)));
        Assert.Equal([1, 0], totals);
        Assert.Equal(2, stored.TurnNumber);
    }

    [PostgresFact]
    public async Task A_player_leaving_when_the_others_have_locked_lets_the_next_sweep_resolve_the_round()
    {
        var (alice, bob, carol) = (await GuestAsync("Alice"), await GuestAsync("Bob"), await GuestAsync("Carol"));
        var room = await StartedAsync(alice, bob, carol);
        ToOpen();
        await OkAsync(await PickAsync(alice, room, 1, locks: true));
        await OkAsync(await PickAsync(bob, room, 2, locks: true));

        (await carol.Client.PostAsync($"/api/rooms/{CodeOf(room)}/leave", null)).Dispose();

        Assert.Single(await SweepAsync());
        Assert.Equal(2, (await StoredAsync(room)).TurnNumber);
    }

    [PostgresFact]
    public async Task Chog_race_still_plays_in_turns_beside_a_game_in_rounds()
    {
        var (alice, bob) = (await GuestAsync("Alice"), await GuestAsync("Bob"));
        using var created = await alice.Client.PostAsJsonAsync("/api/rooms", new { gameType = "chog-race" });
        var code = (await created.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("code").GetString()!;
        (await bob.Client.PostAsync($"/api/rooms/{code}/join", null)).Dispose();
        foreach (var player in new[] { alice, bob })
        {
            (await player.Client.PutAsJsonAsync($"/api/rooms/{code}/ready", new { ready = true })).Dispose();
        }

        (await alice.Client.PostAsync($"/api/rooms/{code}/start", null)).Dispose();
        var room = await ViewAsync(alice, code);

        Assert.Equal("Turns", room.GetProperty("play").GetString());
        Assert.Equal(JsonValueKind.Null, room.GetProperty("roundOpensAt").ValueKind);
        Assert.Single(room.GetProperty("members").EnumerateArray(), m => m.GetProperty("isTurn").GetBoolean());
    }

    [PostgresTheory]
    [InlineData("""UPDATE "GameRooms" SET "RoundOpensAt" = NULL""", "CK_GameRooms_Round")]
    [InlineData("""UPDATE "GameRooms" SET "RoundVersion" = NULL""", "CK_GameRooms_Round")]
    [InlineData("""UPDATE "GameRooms" SET "RoundVersion" = "Version" + 1""", "CK_GameRooms_Round")]
    [InlineData("""UPDATE "GameRooms" SET "RoundOpensAt" = "TurnEndsAt" + interval '1 second'""", "CK_GameRooms_Round")]
    [InlineData("""UPDATE "GameRooms" SET "Play" = 'Turns'""", "CK_GameRooms_CurrentTurn")]
    [InlineData("""UPDATE "GameRooms" SET "CurrentPlayerId" = "HostPlayerId" """, "CK_GameRooms_CurrentTurn")]
    [InlineData("""UPDATE "RoomMembers" SET "RoundLocked" = true, "RoundSubmitted" = false""", "CK_RoomMembers_Round")]
    [InlineData("""UPDATE "RoomMembers" SET "RoundSubmitted" = true, "Seat" = NULL""", "CK_RoomMembers_Round")]
    public async Task Database_refuses_inconsistent_rounds(string sql, string constraint)
    {
        var (alice, bob) = (await GuestAsync("Alice"), await GuestAsync("Bob"));
        await StartedAsync(alice, bob);
        await using var db = database.CreateDbContext();

        var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(sql));

        Assert.Equal(("23514", constraint), (error.SqlState, error.ConstraintName));
    }

    [PostgresTheory]
    [InlineData("""UPDATE "GameRooms" SET "Play" = 'Sometimes'""", "CK_GameRooms_Play")]
    [InlineData("""UPDATE "GameRooms" SET "Play" = 'Rounds', "RoundOpensAt" = now(), "RoundVersion" = 0""", "CK_GameRooms_Round")]
    public async Task Database_refuses_a_round_on_a_waiting_room(string sql, string constraint)
    {
        var alice = await GuestAsync("Alice");
        (await alice.Client.PostAsJsonAsync("/api/rooms", new { gameType = "chog-life" })).Dispose();
        await using var db = database.CreateDbContext();

        var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(sql));

        Assert.Equal(("23514", constraint), (error.SqlState, error.ConstraintName));
    }

    /// <summary>
    /// A stand-in game in rounds: <c>PICK</c> <c>{"n", "lock"}</c> keeps the player's secret number (negative ones are
    /// refused); a round adds each player's pick, or 0, to their total and counts the resolution; the third round ends the
    /// match, highest total first. Each player sees the totals and only their own pick.
    /// </summary>
    private sealed class SecretRounds : IRoundMatchRules
    {
        public const int Rounds = 3;

        public string GameType => "chog-life";

        public MatchRoundTiming RoundTiming => Timing;

        public TimeSpan? TimeLimit => TimeSpan.FromMinutes(5);

        public JsonElement? StartState(MatchStartContext context) =>
            Serialize(new Data(new int[context.Match.Members.Count], new int?[context.Match.Members.Count], 0));

        public MatchRuleDecision Apply(MatchCommandContext context)
        {
            if (context.Type != "PICK")
            {
                return MatchRuleDecision.Unknown;
            }

            var n = context.Payload.GetProperty("n").GetInt32();
            if (n < 0)
            {
                return MatchRuleDecision.Reject("negative");
            }

            var data = Read(context.State!.Value);
            data.Picks[SeatOf(context.Match, context.PlayerId) - 1] = n;
            return MatchRuleDecision.Submit(Serialize(data), context.Payload.GetProperty("lock").GetBoolean());
        }

        public MatchRoundResolution ResolveRound(MatchRoundContext context)
        {
            var data = Read(context.State!.Value);
            foreach (var member in context.Match.Members.Where(m => !m.HasLeft))
            {
                data.Totals[member.Seat!.Value - 1] += data.Picks[member.Seat.Value - 1] ?? 0;
            }

            var next = Serialize(new Data(data.Totals, new int?[data.Picks.Length], data.Resolutions + 1));
            if (context.Match.TurnNumber < Rounds)
            {
                return new MatchRoundResolution(next);
            }

            var playing = context.Match.Members.Where(m => !m.HasLeft).ToList();
            return new MatchRoundResolution(next, [.. playing.Select(m => new MatchStanding(
                m.PlayerId, 1 + playing.Count(o => data.Totals[o.Seat!.Value - 1] > data.Totals[m.Seat!.Value - 1])))]);
        }

        public MatchEnding? EndMatch(MatchEndContext context) =>
            new(context.State!.Value, [.. context.Match.Members.Where(m => !m.HasLeft).Select(m => new MatchStanding(m.PlayerId, 1))]);

        public JsonElement? TimeOutTurn(MatchTurnContext context) => throw new InvalidOperationException("Rounds have no turns.");

        public JsonElement? ViewFor(MatchViewContext context)
        {
            var data = Read(context.State);
            var seat = SeatOf(context.Match, context.PlayerId);
            return JsonSerializer.SerializeToElement(new { totals = data.Totals, resolutions = data.Resolutions, mine = data.Picks[seat - 1] });
        }

        private static int SeatOf(GameRoom match, Guid playerId) => match.Members.Single(m => m.PlayerId == playerId).Seat!.Value;

        private sealed record Data(int[] Totals, int?[] Picks, int Resolutions);

        private static Data Read(JsonElement state) => state.Deserialize<Data>(Json)!;

        private static JsonElement Serialize(Data data) => JsonSerializer.SerializeToElement(data, Json);
    }
}
