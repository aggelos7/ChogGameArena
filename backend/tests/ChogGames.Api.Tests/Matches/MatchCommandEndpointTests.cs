using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ChogGames.Core.Matches;
using ChogGames.Core.Rooms;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace ChogGames.Api.Tests.Matches;

public sealed class MatchCommandEndpointTests(MigratedPostgresDatabase database) : IClassFixture<MigratedPostgresDatabase>, IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private ApiFactory _factory = null!;

    private sealed record RoomDto(string Code, Guid MatchId, long Version, string GameType, string Status);

    private sealed record CommandDto(Guid ActionId, string Type, long Version, long CurrentVersion, bool Replayed);

    private sealed record ProblemDto(int Status, string? Code, long? CurrentVersion, string? Reason, Dictionary<string, string[]>? Errors);

    public async Task InitializeAsync()
    {
        if (database.ConnectionString.Length > 0)
        {
            await using var db = database.CreateDbContext();
            await db.Database.ExecuteSqlRawAsync("TRUNCATE \"GuestSessions\", \"Players\" CASCADE");
        }

        // Chog Life plays the counter game below; Chog Race has no rules, like every game in production today (DEC-026).
        _factory = new ApiFactory(
            database.ConnectionString,
            new Dictionary<string, string?> { ["Games:Items:0:Status"] = "Available", ["Games:Items:1:Status"] = "Available" },
            services => services.AddSingleton<IMatchRules, CounterRules>());
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

    /// <summary>A waiting room of <paramref name="players"/>, the first hosting.</summary>
    private static async Task<RoomDto> WaitingAsync(string gameType, params HttpClient[] players)
    {
        using var created = await players[0].PostAsJsonAsync("/api/rooms", new { gameType });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var room = (await created.Content.ReadFromJsonAsync<RoomDto>(Json))!;
        foreach (var player in players[1..])
        {
            using var joined = await player.PostAsync($"/api/rooms/{room.Code}/join", null);
            Assert.Equal(HttpStatusCode.OK, joined.StatusCode);
        }

        return await ViewAsync(players[0], room.Code);
    }

    /// <summary>A started match of <paramref name="players"/>, the first hosting; everyone plays a standard Chog.</summary>
    private static async Task<RoomDto> StartedAsync(string gameType, params HttpClient[] players)
    {
        var room = await WaitingAsync(gameType, players);
        foreach (var player in players)
        {
            using var ready = await player.PutAsJsonAsync($"/api/rooms/{room.Code}/ready", new { ready = true });
            Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        }

        using var started = await players[0].PostAsync($"/api/rooms/{room.Code}/start", null);
        Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        return await ViewAsync(players[0], room.Code);
    }

    private async Task<(RoomDto Room, HttpClient Alice, HttpClient Bob)> MatchAsync(string gameType = "chog-life")
    {
        var alice = await GuestAsync("Alice");
        var bob = await GuestAsync("Bob");
        return (await StartedAsync(gameType, alice, bob), alice, bob);
    }

    private static object Command(RoomDto room, Guid? actionId = null, string type = "ADD", object? payload = null, long? expectedVersion = null) =>
        new { actionId = actionId ?? Guid.NewGuid(), matchId = room.MatchId, expectedVersion = expectedVersion ?? room.Version, type, payload };

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, string code, object body) =>
        client.PostAsJsonAsync($"/api/rooms/{code}/commands", body);

    private static Task<HttpResponseMessage> SendRawAsync(HttpClient client, string code, string json) =>
        client.PostAsync($"/api/rooms/{code}/commands", new StringContent(json, Encoding.UTF8, "application/json"));

    private static async Task<CommandDto> AcceptedAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<CommandDto>(Json))!;
    }

    private static async Task<ProblemDto> ProblemAsync(HttpResponseMessage response, HttpStatusCode status, string? code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = (await response.Content.ReadFromJsonAsync<ProblemDto>(Json))!;
        Assert.Equal(((int)status, code), (problem.Status, problem.Code));
        return problem;
    }

    /// <summary>The room's stored version, game state and recorded actions.</summary>
    private async Task<(long Version, int? Count, int Actions)> StoredAsync(RoomDto room)
    {
        await using var db = database.CreateDbContext();
        var stored = await db.GameRooms.AsNoTracking().SingleAsync(r => r.Id == room.MatchId);
        int? count = stored.State is null ? null : JsonDocument.Parse(stored.State).RootElement.GetProperty("count").GetInt32();
        return (stored.Version, count, await db.MatchActions.CountAsync(a => a.RoomId == room.MatchId));
    }

    [PostgresFact]
    public async Task Room_shows_its_match_id_and_a_version_bumped_by_each_change()
    {
        var alice = await GuestAsync("Alice");
        var bob = await GuestAsync("Bob");
        using (alice)
        using (bob)
        {
            using var created = await alice.PostAsJsonAsync("/api/rooms", new { gameType = "chog-life" });
            var room = (await created.Content.ReadFromJsonAsync<RoomDto>(Json))!;
            Assert.Equal(0, room.Version);

            (await bob.PostAsync($"/api/rooms/{room.Code}/join", null)).Dispose();
            (await bob.PutAsJsonAsync($"/api/rooms/{room.Code}/ready", new { ready = true })).Dispose();
            (await bob.PutAsJsonAsync($"/api/rooms/{room.Code}/ready", new { ready = true })).Dispose();

            var seen = await ViewAsync(alice, room.Code);
            Assert.Equal((room.MatchId, 2L), (seen.MatchId, seen.Version));
            Assert.Equal(2, (await StoredAsync(room)).Version); // the match id is the stored room's id
        }
    }

    [PostgresFact]
    public async Task An_accepted_command_changes_the_match_once_and_bumps_the_version()
    {
        var (room, alice, bob) = await MatchAsync();
        using (alice)
        using (bob)
        {
            using var response = await SendAsync(alice, room.Code, Command(room, payload: new { by = 3 }));

            var result = await AcceptedAsync(response);
            Assert.Equal(("ADD", room.Version + 1, room.Version + 1, false), (result.Type, result.Version, result.CurrentVersion, result.Replayed));
            Assert.Equal((room.Version + 1, (int?)3, 1), await StoredAsync(room));
            Assert.Equal(room.Version + 1, (await ViewAsync(alice, room.Code)).Version);
        }
    }

    [PostgresFact]
    public async Task A_retry_of_the_same_action_returns_its_first_result_and_changes_nothing()
    {
        var (room, alice, bob) = await MatchAsync();
        using (alice)
        using (bob)
        {
            var actionId = Guid.NewGuid();
            var first = await AcceptedAsync(await SendAsync(alice, room.Code, Command(room, actionId, payload: new { by = 2, note = "x" })));

            // The same action, its payload written with other key order and spacing.
            var json = $$$"""{"actionId":"{{{actionId}}}","matchId":"{{{room.MatchId}}}","expectedVersion":{{{room.Version}}},"type":"ADD","payload":{ "note": "x",  "by": 2 }}""";
            using var retry = await SendRawAsync(alice, room.Code, json);

            var replayed = await AcceptedAsync(retry);
            Assert.Equal(first with { Replayed = true }, replayed);
            Assert.Equal((room.Version + 1, (int?)2, 1), await StoredAsync(room));
        }
    }

    [PostgresFact]
    public async Task A_retry_after_the_match_moved_on_still_gets_its_result_with_the_current_version()
    {
        var (room, alice, bob) = await MatchAsync();
        using (alice)
        using (bob)
        {
            var actionId = Guid.NewGuid();
            var first = await AcceptedAsync(await SendAsync(alice, room.Code, Command(room, actionId)));
            await AcceptedAsync(await SendAsync(alice, room.Code, Command(room, expectedVersion: first.Version)));

            var replayed = await AcceptedAsync(await SendAsync(alice, room.Code, Command(room, actionId)));

            Assert.Equal((first.Version, first.Version + 1, true), (replayed.Version, replayed.CurrentVersion, replayed.Replayed));
            Assert.Equal((first.Version + 1, (int?)2, 2), await StoredAsync(room));
        }
    }

    [PostgresFact]
    public async Task Reusing_an_action_id_for_another_command_is_a_conflict()
    {
        var (room, alice, bob) = await MatchAsync();
        using (alice)
        using (bob)
        {
            var actionId = Guid.NewGuid();
            await AcceptedAsync(await SendAsync(alice, room.Code, Command(room, actionId, payload: new { by = 1 })));

            await ProblemAsync(await SendAsync(alice, room.Code, Command(room, actionId, payload: new { by = 5 })), HttpStatusCode.Conflict, "action_conflict");
            await ProblemAsync(await SendAsync(alice, room.Code, Command(room, actionId, type: "DOUBLE", payload: new { by = 1 })), HttpStatusCode.Conflict, "action_conflict");
            await ProblemAsync(await SendAsync(bob, room.Code, Command(room, actionId, payload: new { by = 1 })), HttpStatusCode.Conflict, "action_conflict");

            Assert.Equal((room.Version + 1, (int?)1, 1), await StoredAsync(room));
        }
    }

    [PostgresFact]
    public async Task A_command_built_on_an_older_version_is_stale()
    {
        var (room, alice, bob) = await MatchAsync();
        using (alice)
        using (bob)
        {
            await AcceptedAsync(await SendAsync(alice, room.Code, Command(room)));

            var stale = await ProblemAsync(await SendAsync(bob, room.Code, Command(room)), HttpStatusCode.Conflict, "stale_version");
            var ahead = await ProblemAsync(
                await SendAsync(bob, room.Code, Command(room, expectedVersion: room.Version + 5)), HttpStatusCode.Conflict, "stale_version");

            Assert.Equal((room.Version + 1, room.Version + 1), (stale.CurrentVersion, ahead.CurrentVersion));
            Assert.Equal((room.Version + 1, (int?)1, 1), await StoredAsync(room));
        }
    }

    [PostgresFact]
    public async Task Of_concurrent_commands_on_the_same_version_exactly_one_succeeds()
    {
        var (room, alice, bob) = await MatchAsync();
        using (alice)
        using (bob)
        {
            var sends = Enumerable.Range(0, 8).Select(_ => SendAsync(alice, room.Code, Command(room))).ToArray();
            var responses = await Task.WhenAll(sends);

            Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK);
            foreach (var refused in responses.Where(r => r.StatusCode != HttpStatusCode.OK))
            {
                await ProblemAsync(refused, HttpStatusCode.Conflict, "stale_version");
            }

            Assert.Equal((room.Version + 1, (int?)1, 1), await StoredAsync(room));
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
    }

    [PostgresFact]
    public async Task Concurrent_retries_of_one_action_apply_it_once()
    {
        var (room, alice, bob) = await MatchAsync();
        using (alice)
        using (bob)
        {
            var command = Command(room, Guid.NewGuid());
            var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => SendAsync(alice, room.Code, command)));

            var results = await Task.WhenAll(responses.Select(AcceptedAsync));
            Assert.Single(results, r => !r.Replayed);
            Assert.All(results, r => Assert.Equal(room.Version + 1, r.Version));
            Assert.Equal((room.Version + 1, (int?)1, 1), await StoredAsync(room));
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
    }

    [PostgresFact]
    public async Task Commands_need_a_started_match()
    {
        var alice = await GuestAsync("Alice");
        var bob = await GuestAsync("Bob");
        using (alice)
        using (bob)
        {
            var room = await WaitingAsync("chog-life", alice, bob);

            await ProblemAsync(await SendAsync(alice, room.Code, Command(room)), HttpStatusCode.Conflict, "match_not_started");

            Assert.Equal((room.Version, (int?)null, 0), await StoredAsync(room));
        }
    }

    [PostgresFact]
    public async Task A_player_who_left_the_match_cannot_act_in_it()
    {
        var alice = await GuestAsync("Alice");
        var bob = await GuestAsync("Bob");
        var carol = await GuestAsync("Carol");
        using (alice)
        using (bob)
        using (carol)
        {
            var room = await StartedAsync("chog-life", alice, bob, carol);
            (await carol.PostAsync($"/api/rooms/{room.Code}/leave", null)).Dispose();
            room = await ViewAsync(alice, room.Code);

            await ProblemAsync(await SendAsync(carol, room.Code, Command(room)), HttpStatusCode.Forbidden, "not_in_match");

            Assert.Equal((room.Version, (int?)null, 0), await StoredAsync(room));
        }
    }

    [PostgresFact]
    public async Task A_forfeit_makes_commands_built_before_it_stale()
    {
        var alice = await GuestAsync("Alice");
        var bob = await GuestAsync("Bob");
        var carol = await GuestAsync("Carol");
        using (alice)
        using (bob)
        using (carol)
        {
            var room = await StartedAsync("chog-life", alice, bob, carol);
            (await carol.PostAsync($"/api/rooms/{room.Code}/leave", null)).Dispose();

            var stale = await ProblemAsync(await SendAsync(alice, room.Code, Command(room)), HttpStatusCode.Conflict, "stale_version");

            Assert.Equal(room.Version + 1, stale.CurrentVersion);
        }
    }

    [PostgresFact]
    public async Task An_ended_match_takes_no_commands()
    {
        var (room, alice, bob) = await MatchAsync();
        using (alice)
        using (bob)
        {
            (await bob.PostAsync($"/api/rooms/{room.Code}/leave", null)).Dispose();
            room = await ViewAsync(alice, room.Code);
            Assert.Equal(nameof(RoomStatus.Abandoned), room.Status);

            await ProblemAsync(await SendAsync(alice, room.Code, Command(room)), HttpStatusCode.Conflict, "room_closed");
        }
    }

    [PostgresFact]
    public async Task Other_players_matches_look_like_unknown_rooms()
    {
        var (room, alice, bob) = await MatchAsync();
        using var mallory = await GuestAsync("Mallory");
        using (alice)
        using (bob)
        {
            await ProblemAsync(await SendAsync(mallory, room.Code, Command(room)), HttpStatusCode.NotFound, "room_not_found");
            await ProblemAsync(await SendAsync(mallory, "ZZZZZZ", Command(room)), HttpStatusCode.NotFound, "room_not_found");
            await ProblemAsync(await SendAsync(alice, "not-a-code", Command(room)), HttpStatusCode.NotFound, "room_not_found");
            await ProblemAsync(await SendAsync(alice, room.Code, Command(room with { MatchId = Guid.NewGuid() })), HttpStatusCode.NotFound, "room_not_found");

            Assert.Equal((room.Version, (int?)null, 0), await StoredAsync(room));
        }
    }

    [PostgresFact]
    public async Task The_games_rules_decide_unknown_and_refused_commands()
    {
        var (room, alice, bob) = await MatchAsync();
        using (alice)
        using (bob)
        {
            await ProblemAsync(await SendAsync(alice, room.Code, Command(room, type: "FLY")), HttpStatusCode.BadRequest, "unknown_command");
            var refused = await ProblemAsync(
                await SendAsync(alice, room.Code, Command(room, payload: new { by = -1 })), HttpStatusCode.Conflict, "command_rejected");

            Assert.Equal("negative_step", refused.Reason);
            Assert.Equal((room.Version, (int?)null, 0), await StoredAsync(room));
        }
    }

    [PostgresFact]
    public async Task A_refused_command_does_not_use_up_its_action_id()
    {
        var (room, alice, bob) = await MatchAsync();
        using (alice)
        using (bob)
        {
            var actionId = Guid.NewGuid();
            await ProblemAsync(await SendAsync(alice, room.Code, Command(room, actionId, type: "DOUBLE")), HttpStatusCode.Conflict, "command_rejected");

            // Refusals aren't recorded, so the action id is still free.
            await AcceptedAsync(await SendAsync(alice, room.Code, Command(room, actionId)));
        }
    }

    [PostgresFact]
    public async Task A_game_without_rules_has_no_commands()
    {
        var (room, alice, bob) = await MatchAsync("chog-race");
        using (alice)
        using (bob)
        {
            await ProblemAsync(await SendAsync(alice, room.Code, Command(room)), HttpStatusCode.BadRequest, "unknown_command");

            Assert.Equal((room.Version, (int?)null, 0), await StoredAsync(room));
        }
    }

    public static TheoryData<string, string> InvalidEnvelopes => new()
    {
        { """{"matchId":"{m}","expectedVersion":{v},"type":"ADD"}""", "actionId" },
        { """{"actionId":"00000000-0000-0000-0000-000000000000","matchId":"{m}","expectedVersion":{v},"type":"ADD"}""", "actionId" },
        { """{"actionId":"{a}","expectedVersion":{v},"type":"ADD"}""", "matchId" },
        { """{"actionId":"{a}","matchId":"{m}","type":"ADD"}""", "expectedVersion" },
        { """{"actionId":"{a}","matchId":"{m}","expectedVersion":-1,"type":"ADD"}""", "expectedVersion" },
        { """{"actionId":"{a}","matchId":"{m}","expectedVersion":{v}}""", "type" },
        { """{"actionId":"{a}","matchId":"{m}","expectedVersion":{v},"type":"add"}""", "type" },
        { """{"actionId":"{a}","matchId":"{m}","expectedVersion":{v},"type":"ADD","payload":[1]}""", "payload" },
        { """{"actionId":"{a}","matchId":"{m}","expectedVersion":{v},"type":"ADD","payload":"by"}""", "payload" },
        { """{"actionId":"{a}","matchId":"{m}","expectedVersion":{v},"type":"ADD","payload":{"pad":"{big}"}}""", "payload" },
    };

    [PostgresTheory]
    [MemberData(nameof(InvalidEnvelopes))]
    public async Task Invalid_envelopes_are_refused_without_a_change(string template, string field)
    {
        var (room, alice, bob) = await MatchAsync();
        using (alice)
        using (bob)
        {
            var json = template
                .Replace("{a}", Guid.NewGuid().ToString())
                .Replace("{m}", room.MatchId.ToString())
                .Replace("{v}", room.Version.ToString())
                .Replace("{big}", new string('x', MatchCommand.MaxPayloadLength));

            var problem = await ProblemAsync(await SendRawAsync(alice, room.Code, json), HttpStatusCode.BadRequest, null);

            Assert.Equal([field], problem.Errors!.Keys);
            Assert.Equal((room.Version, (int?)null, 0), await StoredAsync(room));
        }
    }

    [PostgresFact]
    public async Task Commands_need_a_session()
    {
        var (room, alice, bob) = await MatchAsync();
        using (alice)
        using (bob)
        using (var anonymous = _factory.CreateHttpsClient())
        {
            using var response = await SendAsync(anonymous, room.Code, Command(room));

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    [PostgresTheory]
    [InlineData("roll", "{}", 1, "CK_MatchActions_Type_Format")]
    [InlineData("ROLL", "[1]", 1, "CK_MatchActions_Payload")]
    [InlineData("ROLL", "{}", 0, "CK_MatchActions_Version")]
    public async Task Database_rejects_invalid_match_actions(string type, string payload, long version, string constraint)
    {
        var (room, alice, bob) = await MatchAsync();
        using (alice)
        using (bob)
        {
            await using var db = database.CreateDbContext();
            var playerId = await db.Players.Select(p => p.Id).FirstAsync();

            var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "MatchActions" ("RoomId", "ActionId", "PlayerId", "Type", "Payload", "Version", "CreatedAt")
                VALUES ({room.MatchId}, {Guid.NewGuid()}, {playerId}, {type}, CAST({payload} AS jsonb), {version}, now())
                """));

            Assert.Equal(("23514", constraint), (error.SqlState, error.ConstraintName));
        }
    }

    [PostgresFact]
    public async Task Database_keeps_one_record_per_action_in_a_match()
    {
        var (room, alice, bob) = await MatchAsync();
        using (alice)
        using (bob)
        {
            var actionId = Guid.NewGuid();
            await AcceptedAsync(await SendAsync(alice, room.Code, Command(room, actionId)));
            await using var db = database.CreateDbContext();
            var playerId = await db.Players.Select(p => p.Id).FirstAsync();

            var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "MatchActions" ("RoomId", "ActionId", "PlayerId", "Type", "Payload", "Version", "CreatedAt")
                VALUES ({room.MatchId}, {actionId}, {playerId}, 'ADD', CAST({"{}"} AS jsonb), 9, now())
                """));

            Assert.Equal(PostgresErrorCodes.UniqueViolation, error.SqlState);
        }
    }

    [PostgresTheory]
    [InlineData(false, "-1", null, "CK_GameRooms_Version")]
    [InlineData(true, "0", "[1]", "CK_GameRooms_State")] // state is an object
    [InlineData(false, "0", "{}", "CK_GameRooms_State")] // only a started match has state
    public async Task Database_rejects_invalid_versions_and_state(bool started, string version, string? state, string constraint)
    {
        var alice = await GuestAsync("Alice");
        var bob = await GuestAsync("Bob");
        using (alice)
        using (bob)
        {
            var room = started ? await StartedAsync("chog-life", alice, bob) : await WaitingAsync("chog-life", alice, bob);
            await using var db = database.CreateDbContext();

            var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE "GameRooms" SET "Version" = CAST({version} AS bigint), "State" = CAST({state} AS jsonb) WHERE "Id" = {room.MatchId}
                """));

            Assert.Equal(("23514", constraint), (error.SqlState, error.ConstraintName));
        }
    }

    /// <summary>
    /// A stand-in game: <c>ADD</c> adds <c>by</c> (default 1) to a counter and refuses negative steps; <c>DOUBLE</c> is
    /// known but never allowed. Like real rules, it is deterministic and keeps no state of its own. Turns are covered by
    /// <see cref="MatchTurnTests"/>.
    /// </summary>
    private sealed class CounterRules : IMatchRules
    {
        public string GameType => "chog-life";

        public MatchRuleDecision Apply(MatchCommandContext context)
        {
            switch (context.Type)
            {
                case "ADD":
                    var by = context.Payload.TryGetProperty("by", out var step) ? step.GetInt32() : 1;
                    if (by < 0)
                    {
                        return MatchRuleDecision.Reject("negative_step");
                    }

                    var count = context.State?.GetProperty("count").GetInt32() ?? 0;
                    return MatchRuleDecision.Accept(JsonSerializer.SerializeToElement(new { count = count + by }));
                case "DOUBLE":
                    return MatchRuleDecision.Reject("not_allowed");
                default:
                    return MatchRuleDecision.Unknown;
            }
        }

        public JsonElement? TimeOutTurn(MatchTurnContext context) => null;
    }
}
