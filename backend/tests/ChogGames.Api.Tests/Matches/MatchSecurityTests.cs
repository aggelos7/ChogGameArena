using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ChogGames.Api.Matches;
using ChogGames.Core.Matches;
using ChogGames.Infrastructure.Matches;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ChogGames.Api.Tests.Matches;

/// <summary>
/// Server-side randomness and forged commands (STORY-029, ADR-003, ENG-003, ENG-008, DEC-031): rolls and turn order come
/// only from the server's <see cref="IRandomSource"/>, whatever the client sends, and commands from the wrong player,
/// room, turn or version are refused before the game's rules run, changing nothing and drawing nothing.
/// </summary>
public sealed class MatchSecurityTests(MigratedPostgresDatabase database) : IClassFixture<MigratedPostgresDatabase>, IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan Turn = TimeSpan.FromSeconds(30);

    // Whole seconds, so times read back from Postgres (microseconds) compare equal.
    private readonly ManualTimeProvider _clock = new(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
    private readonly ScriptedRandomSource _random = new();
    private readonly DiceRules _rules = new();

    private ApiFactory _factory = null!;

    private sealed record MemberDto(string DisplayName, bool IsTurn);

    private sealed record RoomDto(string Code, Guid MatchId, long Version, string Status, MemberDto[] Members, JsonElement? State)
    {
        public string? TurnOf => Members.SingleOrDefault(m => m.IsTurn)?.DisplayName;
    }

    private sealed record ProblemDto(int Status, string? Code);

    public async Task InitializeAsync()
    {
        if (database.ConnectionString.Length > 0)
        {
            await using var db = database.CreateDbContext();
            await db.Database.ExecuteSqlRawAsync("TRUNCATE \"GuestSessions\", \"Players\" CASCADE");
        }

        var settings = new Dictionary<string, string?>
        {
            ["Games:Items:0:Status"] = "Available",
            ["Matches:TurnTimeout"] = Turn.ToString(),
        };

        // Chog Life plays the dice game below with the scripted source; the tests time out turns themselves.
        _factory = new ApiFactory(database.ConnectionString, settings, services =>
        {
            services.AddSingleton<TimeProvider>(_clock);
            services.AddSingleton<IRandomSource>(_random);
            services.AddSingleton<IMatchRules>(_rules);
            foreach (var worker in services.Where(d => d.ImplementationType == typeof(TurnTimeoutWorker)).ToList())
            {
                services.Remove(worker);
            }
        });
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    private async Task<HttpClient> GuestAsync(string name)
    {
        var client = _factory.CreateHttpsClient();
        using var response = await client.PostAsJsonAsync("/api/guest-sessions", new { displayName = name });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return client;
    }

    private static async Task<RoomDto> ViewAsync(HttpClient client, string code) =>
        (await client.GetFromJsonAsync<RoomDto>($"/api/rooms/{code}", Json))!;

    /// <summary>A waiting room hosted by the first player, the others joined and everyone ready.</summary>
    private async Task<RoomDto> ReadyRoomAsync(params HttpClient[] players)
    {
        using var created = await players[0].PostAsJsonAsync("/api/rooms", new { gameType = "chog-life" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var room = (await created.Content.ReadFromJsonAsync<RoomDto>(Json))!;
        foreach (var player in players[1..])
        {
            _clock.Advance(TimeSpan.FromSeconds(1));
            using var joined = await player.PostAsync($"/api/rooms/{room.Code}/join", null);
            Assert.Equal(HttpStatusCode.OK, joined.StatusCode);
        }

        foreach (var player in players)
        {
            using var ready = await player.PutAsJsonAsync($"/api/rooms/{room.Code}/ready", new { ready = true });
            Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        }

        return room;
    }

    /// <summary>A started match; with nothing scripted, seats keep join order, so the host plays first.</summary>
    private async Task<RoomDto> StartedAsync(params HttpClient[] players)
    {
        var room = await ReadyRoomAsync(players);
        using var started = await players[0].PostAsync($"/api/rooms/{room.Code}/start", null);
        Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        return await ViewAsync(players[0], room.Code);
    }

    private static Task<HttpResponseMessage> SendAsync(
        HttpClient client, RoomDto room, string type, object? payload = null, Guid? actionId = null, long? expectedVersion = null, Guid? matchId = null) =>
        client.PostAsJsonAsync(
            $"/api/rooms/{room.Code}/commands",
            new
            {
                actionId = actionId ?? Guid.NewGuid(),
                matchId = matchId ?? room.MatchId,
                expectedVersion = expectedVersion ?? room.Version,
                type,
                payload = payload ?? new { },
            });

    private static async Task ExpectAsync(HttpResponseMessage response, HttpStatusCode status, string? code = null)
    {
        using (response)
        {
            Assert.Equal(status, response.StatusCode);
            if (code is not null)
            {
                var problem = (await response.Content.ReadFromJsonAsync<ProblemDto>(Json))!;
                Assert.Equal(((int)status, code), (problem.Status, problem.Code));
            }
        }
    }

    /// <summary>The room's stored version, turn, current player and game state, and its recorded actions.</summary>
    private async Task<(long Version, int TurnNumber, Guid? CurrentPlayer, string? State, int Actions)> StoredAsync(Guid matchId)
    {
        await using var db = database.CreateDbContext();
        var room = await db.GameRooms.AsNoTracking().SingleAsync(r => r.Id == matchId);
        var actions = await db.MatchActions.CountAsync(a => a.RoomId == matchId);
        return (room.Version, room.TurnNumber, room.CurrentPlayerId, room.State, actions);
    }

    private async Task<Guid> PlayerIdAsync(string displayName)
    {
        await using var db = database.CreateDbContext();
        return await db.Players.Where(p => p.DisplayName == displayName).Select(p => p.Id).SingleAsync();
    }

    [PostgresFact]
    public async Task A_client_supplied_roll_is_ignored_and_the_server_rolls()
    {
        var alice = await GuestAsync("Alice");
        var bob = await GuestAsync("Bob");
        using (alice)
        using (bob)
        {
            var room = await StartedAsync(alice, bob);
            _random.Enqueue(2);
            var drawsBefore = _random.Draws.Count;

            await ExpectAsync(
                await SendAsync(alice, room, "ROLL", new { roll = 6, value = 6, dice = new[] { 6, 6 }, total = 99, lastRoll = 6 }),
                HttpStatusCode.OK);

            Assert.Equal([(1, 7)], _random.Draws.Skip(drawsBefore));
            var state = (await ViewAsync(bob, room.Code)).State!.Value;
            Assert.Equal((2, 2, 1), (state.GetProperty("lastRoll").GetInt32(), state.GetProperty("total").GetInt32(), state.GetProperty("rolls").GetInt32()));
        }
    }

    [PostgresFact]
    public async Task A_retried_roll_returns_its_first_result_without_rolling_again()
    {
        var alice = await GuestAsync("Alice");
        var bob = await GuestAsync("Bob");
        using (alice)
        using (bob)
        {
            var room = await StartedAsync(alice, bob);
            _random.Enqueue(1);
            var actionId = Guid.NewGuid();
            await ExpectAsync(await SendAsync(alice, room, "ROLL", actionId: actionId), HttpStatusCode.OK);
            var drawsAfterFirst = _random.Draws.Count;
            var stored = await StoredAsync(room.MatchId);

            // Resending the same action, hoping for a better roll, replays the first result.
            _random.Enqueue(6);
            using var retry = await SendAsync(alice, room, "ROLL", actionId: actionId);
            Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
            Assert.True((await retry.Content.ReadFromJsonAsync<MatchCommandEndpoints.MatchCommandResponse>(Json))!.Replayed);

            Assert.Equal(drawsAfterFirst, _random.Draws.Count);
            Assert.Equal(stored, await StoredAsync(room.MatchId));
            Assert.Equal(1, (await ViewAsync(alice, room.Code)).State!.Value.GetProperty("lastRoll").GetInt32());
        }
    }

    [PostgresFact]
    public async Task A_player_id_in_the_command_is_ignored_and_the_session_player_acts()
    {
        var alice = await GuestAsync("Alice");
        var bob = await GuestAsync("Bob");
        using (alice)
        using (bob)
        {
            var room = await StartedAsync(alice, bob);
            var aliceId = await PlayerIdAsync("Alice");
            var bobId = await PlayerIdAsync("Bob");

            // Bob, out of turn, claims to be Alice: still Bob, so not his turn.
            using var forged = await bob.PostAsJsonAsync(
                $"/api/rooms/{room.Code}/commands",
                new { actionId = Guid.NewGuid(), matchId = room.MatchId, expectedVersion = room.Version, type = "ROLL", playerId = aliceId, payload = new { playerId = aliceId } });
            await ExpectAsync(forged, HttpStatusCode.Conflict, MatchCommandEndpoints.ErrorCodes.NotYourTurn);

            // Alice claiming to be Bob still acts as Alice.
            var actionId = Guid.NewGuid();
            using var claimed = await alice.PostAsJsonAsync(
                $"/api/rooms/{room.Code}/commands",
                new { actionId, matchId = room.MatchId, expectedVersion = room.Version, type = "ROLL", playerId = bobId, payload = new { playerId = bobId } });
            await ExpectAsync(claimed, HttpStatusCode.OK);

            await using var db = database.CreateDbContext();
            Assert.Equal(aliceId, (await db.MatchActions.SingleAsync(a => a.ActionId == actionId)).PlayerId);
            Assert.Equal([aliceId], _rules.Players);
        }
    }

    [PostgresFact]
    public async Task Turn_order_is_drawn_by_the_server_whatever_the_host_asks_for()
    {
        var alice = await GuestAsync("Alice");
        var bob = await GuestAsync("Bob");
        var carol = await GuestAsync("Carol");
        using (alice)
        using (bob)
        using (carol)
        {
            var room = await ReadyRoomAsync(alice, bob, carol);

            // Fisher–Yates over [Alice, Bob, Carol]: swap seat 3 with seat 2, then seat 2 with seat 1: Carol, Alice, Bob.
            _random.Enqueue(1, 0);
            using var started = await alice.PostAsJsonAsync($"/api/rooms/{room.Code}/start", new { seats = new[] { "Alice" }, firstPlayer = "Alice" });
            Assert.Equal(HttpStatusCode.OK, started.StatusCode);

            Assert.Equal([(0, 3), (0, 2)], _random.Draws);
            var view = await ViewAsync(alice, room.Code);
            Assert.Equal("Carol", view.TurnOf);
            await ExpectAsync(await SendAsync(alice, view, "ROLL"), HttpStatusCode.Conflict, MatchCommandEndpoints.ErrorCodes.NotYourTurn);

            // Then Alice, then Bob.
            await ExpectAsync(await SendAsync(carol, view, "ROLL"), HttpStatusCode.OK);
            view = await ViewAsync(alice, room.Code);
            Assert.Equal("Alice", view.TurnOf);
            await ExpectAsync(await SendAsync(alice, view, "ROLL"), HttpStatusCode.OK);
            Assert.Equal("Bob", (await ViewAsync(bob, room.Code)).TurnOf);
        }
    }

    [PostgresFact]
    public async Task A_turn_that_runs_out_is_resolved_with_a_server_roll()
    {
        var alice = await GuestAsync("Alice");
        var bob = await GuestAsync("Bob");
        using (alice)
        using (bob)
        {
            var room = await StartedAsync(alice, bob);
            _random.Enqueue(5);
            _clock.Advance(Turn);

            await using (var scope = _factory.Services.CreateAsyncScope())
            {
                Assert.Single(await scope.ServiceProvider.GetRequiredService<TurnTimeoutService>().TimeOutOverdueTurnsAsync(CancellationToken.None));
            }

            var view = await ViewAsync(bob, room.Code);
            Assert.Equal((5, "Bob"), (view.State!.Value.GetProperty("lastRoll").GetInt32(), view.TurnOf));
        }
    }

    /// <summary>
    /// Every way of sending a command one may not send, for a roll and for an ability: each is refused with its code
    /// before the game's rules run, and the match, its recorded actions and the random source are untouched.
    /// </summary>
    [PostgresTheory]
    [InlineData("outsider", "ROLL")]
    [InlineData("outsider", "USE_ABILITY")]
    [InlineData("other_match", "ROLL")]
    [InlineData("other_match", "USE_ABILITY")]
    [InlineData("not_started", "ROLL")]
    [InlineData("not_started", "USE_ABILITY")]
    [InlineData("not_your_turn", "ROLL")]
    [InlineData("not_your_turn", "USE_ABILITY")]
    [InlineData("forfeited", "ROLL")]
    [InlineData("forfeited", "USE_ABILITY")]
    [InlineData("turn_expired", "ROLL")]
    [InlineData("turn_expired", "USE_ABILITY")]
    [InlineData("stale_version", "ROLL")]
    [InlineData("stale_version", "USE_ABILITY")]
    [InlineData("closed", "ROLL")]
    [InlineData("closed", "USE_ABILITY")]
    public async Task Unauthorized_commands_change_nothing_and_never_reach_the_rules(string scenario, string type)
    {
        var alice = await GuestAsync("Alice");
        var bob = await GuestAsync("Bob");
        var carol = await GuestAsync("Carol");
        var dave = await GuestAsync("Dave");
        using (alice)
        using (bob)
        using (carol)
        using (dave)
        {
            var room = scenario == "not_started" ? await ReadyRoomAsync(alice, bob, carol) : await StartedAsync(alice, bob, carol);
            HttpClient sender = alice;
            long? expectedVersion = null;
            Guid? matchId = null;
            (HttpStatusCode Status, string Code) refusal;
            switch (scenario)
            {
                case "outsider":
                    sender = dave;
                    refusal = (HttpStatusCode.NotFound, "room_not_found");
                    break;
                case "other_match":
                    matchId = Guid.NewGuid();
                    refusal = (HttpStatusCode.NotFound, "room_not_found");
                    break;
                case "not_started":
                    refusal = (HttpStatusCode.Conflict, MatchCommandEndpoints.ErrorCodes.MatchNotStarted);
                    break;
                case "not_your_turn":
                    sender = bob;
                    refusal = (HttpStatusCode.Conflict, MatchCommandEndpoints.ErrorCodes.NotYourTurn);
                    break;
                case "forfeited":
                    // Carol forfeits; Alice and Bob play on, and Carol tries to act on the current version.
                    (await carol.PostAsync($"/api/rooms/{room.Code}/leave", null)).Dispose();
                    sender = carol;
                    refusal = (HttpStatusCode.Forbidden, MatchCommandEndpoints.ErrorCodes.NotInMatch);
                    break;
                case "turn_expired":
                    _clock.Advance(Turn);
                    refusal = (HttpStatusCode.Conflict, MatchCommandEndpoints.ErrorCodes.TurnExpired);
                    break;
                case "stale_version":
                    expectedVersion = room.Version - 1;
                    refusal = (HttpStatusCode.Conflict, MatchCommandEndpoints.ErrorCodes.StaleVersion);
                    break;
                case "closed":
                    // Bob and Carol leave, so the match is abandoned.
                    (await bob.PostAsync($"/api/rooms/{room.Code}/leave", null)).Dispose();
                    (await carol.PostAsync($"/api/rooms/{room.Code}/leave", null)).Dispose();
                    refusal = (HttpStatusCode.Conflict, "room_closed");
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null);
            }

            room = await ViewAsync(alice, room.Code);
            var before = await StoredAsync(room.MatchId);
            var draws = _random.Draws.Count;
            _random.Enqueue(6);

            await ExpectAsync(
                await SendAsync(sender, room, type, new { roll = 6, abilityId = "extra_roll" }, expectedVersion: expectedVersion, matchId: matchId),
                refusal.Status,
                refusal.Code);

            Assert.Equal(before, await StoredAsync(room.MatchId));
            Assert.Equal(draws, _random.Draws.Count);
            Assert.Empty(_rules.Players);
        }
    }

    /// <summary>
    /// A stand-in dice game: <c>ROLL</c> rolls a six-sided die with the server's source, adds it to a total and ends the
    /// turn, ignoring whatever the payload says. A turn that runs out is rolled for the absent player. It has no
    /// abilities, so <c>USE_ABILITY</c> is refused by the rules if it ever gets there. Records which players reached it.
    /// </summary>
    private sealed class DiceRules : IMatchRules
    {
        private readonly List<Guid> _players = [];

        public string GameType => "chog-life";

        public IReadOnlyList<Guid> Players
        {
            get
            {
                lock (_players)
                {
                    return [.. _players];
                }
            }
        }

        public MatchRuleDecision Apply(MatchCommandContext context)
        {
            lock (_players)
            {
                _players.Add(context.PlayerId);
            }

            return context.Type switch
            {
                "ROLL" => MatchRuleDecision.Accept(Rolled(context.State, context.Random), endsTurn: true),
                "USE_ABILITY" => MatchRuleDecision.Reject("no_abilities"),
                _ => MatchRuleDecision.Unknown,
            };
        }

        public JsonElement? TimeOutTurn(MatchTurnContext context) => Rolled(context.State, context.Random);

        public JsonElement? ViewFor(MatchViewContext context) => context.State;

        private static JsonElement Rolled(JsonElement? state, IRandomSource random)
        {
            var roll = random.Next(1, 7);
            var total = state?.GetProperty("total").GetInt32() ?? 0;
            var rolls = state?.GetProperty("rolls").GetInt32() ?? 0;
            return JsonSerializer.SerializeToElement(new { lastRoll = roll, total = total + roll, rolls = rolls + 1 });
        }
    }
}
