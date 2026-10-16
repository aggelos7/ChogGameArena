using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using ChogGames.Api.Matches;
using ChogGames.Api.Realtime;
using ChogGames.Core.Abilities;
using ChogGames.Core.Matches;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ChogGames.Api.Tests.Matches;

/// <summary>
/// Ability execution and usage tracking (STORY-034, NFT-014, NFT-015, NFT-016, DEC-036): <c>USE_ABILITY</c> goes to the
/// shared engine under the room's lock, so only the ability a player locked at start can be used, on their own turn, at
/// most its max uses, with the values locked then; a use is counted once even when sent concurrently, and every refusal
/// changes nothing. Chog Life plays a small shield-and-steal game here; Chog Race's catalog is taken out, as by the test host.
/// </summary>
public sealed class AbilityExecutionTests(MigratedPostgresDatabase database) : IClassFixture<MigratedPostgresDatabase>, IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ScriptedRandomSource _random = new();
    private readonly ShieldHandler _shield = new();
    private readonly StealHandler _steal = new();
    private readonly List<IAsyncDisposable> _disposables = [];
    private ApiFactory _factory = null!;

    private sealed record Guest(string Cookie)
    {
        public HttpClient Client { get; set; } = null!;
    }

    public async Task InitializeAsync()
    {
        if (database.ConnectionString.Length > 0)
        {
            await using var db = database.CreateDbContext();
            await db.Database.ExecuteSqlRawAsync("TRUNCATE \"GuestSessions\", \"Players\" CASCADE");
        }

        _factory = Factory(Catalog(1, Shield(40)));
    }

    public async Task DisposeAsync()
    {
        foreach (var disposable in _disposables)
        {
            await disposable.DisposeAsync();
        }

        await _factory.DisposeAsync();
    }

    private static AbilityDefinition Shield(decimal amount) => AbilityDefinition.Create(
        "shield", "Shield", "Protect your cards this round.", AbilityTargetKind.None, ["own_turn"],
        new Dictionary<string, decimal> { ["amount"] = amount });

    private static AbilityDefinition Steal() => AbilityDefinition.Create(
        "steal", "Steal", "Take a random card from an opponent.", AbilityTargetKind.Opponent, ["own_turn"], maxUses: 2);

    private AbilityCatalog Catalog(int version, AbilityDefinition? shield) =>
        shield is null
            ? AbilityCatalog.Create("chog-life", version, [Steal()], [_steal], new OwnTurn())
            : AbilityCatalog.Create("chog-life", version, [shield, Steal()], [_shield, _steal], new OwnTurn());

    /// <summary>A server with <paramref name="catalog"/> for Chog Life; turns never run out by themselves here.</summary>
    private ApiFactory Factory(AbilityCatalog catalog) =>
        new(
            database.ConnectionString,
            new Dictionary<string, string?> { ["Games:Items:0:Status"] = "Available", ["Games:Items:1:Status"] = "Available" },
            services =>
            {
                services.AddSingleton<IRandomSource>(_random);
                services.RemoveAll<IMatchRules>().AddSingleton<IMatchRules>(new TurnRules());
                services.AddSingleton(catalog);
                foreach (var worker in services.Where(d => d.ImplementationType == typeof(TurnTimeoutWorker)).ToList())
                {
                    services.Remove(worker);
                }
            });

    /// <summary>Another deploy on the same database, with <paramref name="catalog"/>; browsers keep only their cookie.</summary>
    private async Task RedeployAsync(AbilityCatalog catalog, params Guest[] guests)
    {
        await _factory.DisposeAsync();
        _factory = Factory(catalog);
        foreach (var guest in guests)
        {
            guest.Client = Browser(guest.Cookie);
        }
    }

    private HttpClient Browser(string cookie)
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", cookie);
        return client;
    }

    private async Task<Guest> GuestAsync(string name)
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), HandleCookies = false });
        using var response = await client.PostAsJsonAsync("/api/guest-sessions", new { displayName = name });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var cookie = response.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
        client.DefaultRequestHeaders.Add("Cookie", cookie);
        return new Guest(cookie) { Client = client };
    }

    private static async Task<JsonElement> SendAsync(
        Guest guest, HttpMethod method, string path, object? body = null, HttpStatusCode expected = HttpStatusCode.OK)
    {
        using var request = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body) };
        using var response = await guest.Client.SendAsync(request);
        Assert.Equal(expected, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>(Json);
    }

    private static Task<JsonElement> ViewAsync(Guest guest, string code) => SendAsync(guest, HttpMethod.Get, $"/api/rooms/{code}");

    /// <summary>
    /// A started match: the first player hosts, each player locks the ability given with them, and with nothing scripted
    /// seats keep join order, so the host plays first.
    /// </summary>
    private static async Task<string> StartedAsync(string gameType, params (Guest Guest, string? Ability)[] players)
    {
        var created = await SendAsync(players[0].Guest, HttpMethod.Post, "/api/rooms", new { gameType }, HttpStatusCode.Created);
        var code = created.GetProperty("code").GetString()!;
        foreach (var (guest, _) in players[1..])
        {
            await SendAsync(guest, HttpMethod.Post, $"/api/rooms/{code}/join");
        }

        foreach (var (guest, ability) in players)
        {
            if (ability is not null)
            {
                await SendAsync(guest, HttpMethod.Put, $"/api/rooms/{code}/ability", new { abilityId = ability });
            }

            await SendAsync(guest, HttpMethod.Put, $"/api/rooms/{code}/ready", new { ready = true });
        }

        await SendAsync(players[0].Guest, HttpMethod.Post, $"/api/rooms/{code}/start");
        return code;
    }

    private async Task<(string Code, Guest Alice, Guest Bob)> ShieldAndStealAsync()
    {
        var alice = await GuestAsync("Alice");
        var bob = await GuestAsync("Bob");
        var code = await StartedAsync("chog-life", (alice, "shield"), (bob, "steal"));
        return (code, alice, bob);
    }

    private static Task<HttpResponseMessage> CommandAsync(Guest guest, JsonElement room, string type, object? payload = null, Guid? actionId = null) =>
        guest.Client.PostAsJsonAsync($"/api/rooms/{room.GetProperty("code").GetString()}/commands", new
        {
            actionId = actionId ?? Guid.NewGuid(),
            matchId = room.GetProperty("matchId").GetGuid(),
            expectedVersion = room.GetProperty("version").GetInt64(),
            type,
            payload,
        });

    private static async Task<JsonElement> AppliedAsync(Task<HttpResponseMessage> sending)
    {
        using var response = await sending;
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>(Json);
    }

    /// <summary>Asserts the command was refused with <paramref name="code"/> and, for a refused ability, <paramref name="reason"/>.</summary>
    private static async Task RefusedAsync(Task<HttpResponseMessage> sending, HttpStatusCode status, string code, string? reason = null)
    {
        using var response = await sending;
        Assert.Equal(status, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal(code, problem.GetProperty("code").GetString());
        Assert.Equal(reason, problem.TryGetProperty("reason", out var given) ? given.GetString() : null);
    }

    private static JsonElement Member(JsonElement room, string name) =>
        room.GetProperty("members").EnumerateArray().Single(m => m.GetProperty("displayName").GetString() == name);

    private static int? UsesLeft(JsonElement room, string name) =>
        Member(room, name).GetProperty("ability") is { ValueKind: JsonValueKind.Object } ability ? ability.GetProperty("usesLeft").GetInt32() : null;

    private static string? TurnOf(JsonElement room) =>
        room.GetProperty("members").EnumerateArray().Single(m => m.GetProperty("isTurn").GetBoolean()).GetProperty("displayName").GetString();

    /// <summary>
    /// What a refusal must leave alone: the room's version and state, its actions, every member's uses (in seat order)
    /// and the server's draws so far.
    /// </summary>
    private async Task<(long Version, string? State, int Actions, string Uses, int Draws)> StoredAsync(string code)
    {
        await using var db = database.CreateDbContext();
        var room = await db.GameRooms.AsNoTracking().Include(r => r.Members).SingleAsync(r => r.Code == code);
        var actions = await db.MatchActions.CountAsync(a => a.RoomId == room.Id);
        var uses = string.Join(",", room.Members.OrderBy(m => m.Seat).Select(m => m.AbilityUses));
        return (room.Version, room.State, actions, uses, _random.Draws.Count);
    }

    /// <summary>Asserts nothing was stored, drawn or applied by a refused command.</summary>
    private async Task AssertUnchangedAsync(string code, (long, string?, int, string, int) before, int executions)
    {
        Assert.Equal(before, await StoredAsync(code));
        Assert.Equal(executions, _shield.Executions + _steal.Executions);
    }

    [PostgresFact]
    public async Task Ability_is_applied_once_counted_and_pushed_to_everyone()
    {
        var (code, alice, bob) = await ShieldAndStealAsync();
        var listener = await ConnectAsync(bob);
        var room = await listener.SubscribeAsync(code);
        Assert.Equal((1, 1, 2), (UsesLeft(room, "Alice"), Member(room, "Alice").GetProperty("seat").GetInt32(), Member(room, "Bob").GetProperty("seat").GetInt32()));

        var applied = await AppliedAsync(CommandAsync(alice, room, MatchCommand.UseAbility, new { abilityId = "shield" }));

        var pushed = await listener.NextAsync();
        Assert.Equal(room.GetProperty("version").GetInt64() + 1, applied.GetProperty("version").GetInt64());
        Assert.Equal((applied.GetProperty("version").GetInt64(), "USE_ABILITY", false), (pushed.GetProperty("version").GetInt64(), applied.GetProperty("type").GetString(), applied.GetProperty("replayed").GetBoolean()));
        Assert.Equal((0, 2, "Alice"), (UsesLeft(pushed, "Alice"), UsesLeft(pushed, "Bob"), TurnOf(pushed)));
        Assert.Equal(40, pushed.GetProperty("state").GetProperty("shields").GetProperty("1").GetInt32());
        Assert.Equal((pushed.GetProperty("turnNumber").GetInt32(), pushed.GetProperty("turnEndsAt").GetString()), (room.GetProperty("turnNumber").GetInt32(), room.GetProperty("turnEndsAt").GetString()));
        Assert.Equal((1, "1,0"), ((await StoredAsync(code)).Actions, (await StoredAsync(code)).Uses));
        Assert.Equal(1, _shield.Executions);
    }

    [PostgresFact]
    public async Task Duplicate_use_is_refused_and_a_retry_replays_the_first()
    {
        var (code, alice, _) = await ShieldAndStealAsync();
        var actionId = Guid.NewGuid();
        await AppliedAsync(CommandAsync(alice, await ViewAsync(alice, code), MatchCommand.UseAbility, new { abilityId = "shield" }, actionId));
        var room = await ViewAsync(alice, code);
        var before = await StoredAsync(code);

        await RefusedAsync(
            CommandAsync(alice, room, MatchCommand.UseAbility, new { abilityId = "shield" }),
            HttpStatusCode.Conflict, MatchCommandEndpoints.ErrorCodes.CommandRejected, AbilityRefusal.UsedUp);
        var retry = await AppliedAsync(CommandAsync(alice, room, MatchCommand.UseAbility, new { abilityId = "shield" }, actionId));

        Assert.True(retry.GetProperty("replayed").GetBoolean());
        await AssertUnchangedAsync(code, before, executions: 1);
        Assert.Equal(0, UsesLeft(await ViewAsync(alice, code), "Alice"));
    }

    [PostgresTheory]
    [InlineData("""{"abilityId":"steal","target":2}""", AbilityRefusal.InvalidTarget)] // the user
    [InlineData("""{"abilityId":"steal","target":3}""", AbilityRefusal.InvalidTarget)] // nobody
    [InlineData("""{"abilityId":"steal","target":0}""", AbilityRefusal.InvalidTarget)]
    [InlineData("""{"abilityId":"steal"}""", AbilityRefusal.InvalidTarget)]
    [InlineData("""{"abilityId":"steal","target":"1"}""", AbilityRefusal.InvalidPayload)]
    [InlineData("""{"abilityId":"steal","target":1,"card":0}""", AbilityRefusal.InvalidPayload)] // outcomes aren't chosen
    [InlineData("""{"abilityId":"shield"}""", AbilityRefusal.NotYourAbility)]
    [InlineData("""{"abilityId":"card_swap","target":1}""", AbilityRefusal.UnknownAbility)]
    public async Task Invalid_use_changes_nothing_and_draws_nothing(string payload, string reason)
    {
        var (code, alice, bob) = await ShieldAndStealAsync();
        await AppliedAsync(CommandAsync(alice, await ViewAsync(alice, code), "END_TURN"));
        var room = await ViewAsync(bob, code);
        var before = await StoredAsync(code);

        await RefusedAsync(
            CommandAsync(bob, room, MatchCommand.UseAbility, JsonSerializer.Deserialize<JsonElement>(payload)),
            HttpStatusCode.Conflict, MatchCommandEndpoints.ErrorCodes.CommandRejected, reason);

        await AssertUnchangedAsync(code, before, executions: 0);
    }

    [PostgresFact]
    public async Task Abilities_are_used_only_on_your_own_turn()
    {
        var (code, alice, bob) = await ShieldAndStealAsync();
        var room = await ViewAsync(bob, code);
        var before = await StoredAsync(code);

        await RefusedAsync(
            CommandAsync(bob, room, MatchCommand.UseAbility, new { abilityId = "steal", target = 1 }),
            HttpStatusCode.Conflict, MatchCommandEndpoints.ErrorCodes.NotYourTurn);

        await AssertUnchangedAsync(code, before, executions: 0);
        Assert.Equal("Alice", TurnOf(await ViewAsync(alice, code)));
    }

    [PostgresFact]
    public async Task Game_conditions_refuse_with_their_reason_and_nothing_changes()
    {
        var (code, alice, bob) = await ShieldAndStealAsync();
        await AppliedAsync(CommandAsync(alice, await ViewAsync(alice, code), MatchCommand.UseAbility, new { abilityId = "shield" }));
        await AppliedAsync(CommandAsync(alice, await ViewAsync(alice, code), "END_TURN"));
        var before = await StoredAsync(code);

        await RefusedAsync(
            CommandAsync(bob, await ViewAsync(bob, code), MatchCommand.UseAbility, new { abilityId = "steal", target = 1 }),
            HttpStatusCode.Conflict, MatchCommandEndpoints.ErrorCodes.CommandRejected, "target_shielded");

        await AssertUnchangedAsync(code, before, executions: 1);
    }

    [PostgresFact]
    public async Task Each_use_is_counted_up_to_the_locked_max()
    {
        var (code, alice, bob) = await ShieldAndStealAsync();
        await AppliedAsync(CommandAsync(alice, await ViewAsync(alice, code), "END_TURN"));
        var seated = _random.Draws.Count;
        _random.Enqueue(2, 0);

        await AppliedAsync(CommandAsync(bob, await ViewAsync(bob, code), MatchCommand.UseAbility, new { abilityId = "steal", target = 1 }));
        Assert.Equal(1, UsesLeft(await ViewAsync(bob, code), "Bob"));
        await AppliedAsync(CommandAsync(bob, await ViewAsync(bob, code), MatchCommand.UseAbility, new { abilityId = "steal", target = 1 }));
        var room = await ViewAsync(bob, code);
        await RefusedAsync(
            CommandAsync(bob, room, MatchCommand.UseAbility, new { abilityId = "steal", target = 1 }),
            HttpStatusCode.Conflict, MatchCommandEndpoints.ErrorCodes.CommandRejected, AbilityRefusal.UsedUp);

        Assert.Equal((0, "Bob"), (UsesLeft(room, "Bob"), TurnOf(room)));
        Assert.Equal("[2,0]", room.GetProperty("state").GetProperty("stolen").GetRawText());
        Assert.Equal([(0, 3), (0, 3)], _random.Draws.Skip(seated));
        Assert.Equal("0,2", (await StoredAsync(code)).Uses);
    }

    /// <summary>Concurrency is resolved by the room's row lock: of uses sent at once, exactly one is applied and counted.</summary>
    [PostgresFact]
    public async Task Concurrent_uses_resolve_once()
    {
        var (code, alice, _) = await ShieldAndStealAsync();
        var room = await ViewAsync(alice, code);

        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            CommandAsync(alice, room, MatchCommand.UseAbility, new { abilityId = "shield" })));
        var codes = new List<string?>();
        foreach (var response in responses)
        {
            using (response)
            {
                codes.Add(response.IsSuccessStatusCode ? null : (await response.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("code").GetString());
            }
        }

        Assert.Single(codes, c => c is null);
        Assert.All(codes.Where(c => c is not null), c => Assert.Equal(MatchCommandEndpoints.ErrorCodes.StaleVersion, c));
        Assert.Equal((1, "1,0", 1), ((await StoredAsync(code)).Actions, (await StoredAsync(code)).Uses, _shield.Executions));
    }

    [PostgresFact]
    public async Task Concurrent_retries_of_one_use_apply_it_once()
    {
        var (code, alice, _) = await ShieldAndStealAsync();
        var room = await ViewAsync(alice, code);
        var actionId = Guid.NewGuid();

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            AppliedAsync(CommandAsync(alice, room, MatchCommand.UseAbility, new { abilityId = "shield" }, actionId))));

        Assert.Single(results, r => !r.GetProperty("replayed").GetBoolean());
        Assert.Single(results.Select(r => r.GetProperty("version").GetInt64()).Distinct());
        Assert.Equal((1, "1,0", 1), ((await StoredAsync(code)).Actions, (await StoredAsync(code)).Uses, _shield.Executions));
    }

    /// <summary>NFT-015: a deploy that changes the catalog mid-match doesn't change what a locked ability does.</summary>
    [PostgresFact]
    public async Task Locked_values_apply_after_a_catalog_change()
    {
        var (code, alice, bob) = await ShieldAndStealAsync();

        await RedeployAsync(Catalog(2, Shield(10)), alice, bob);
        var room = await ViewAsync(alice, code);
        Assert.Equal(1, UsesLeft(room, "Alice"));
        var used = await AppliedAsync(CommandAsync(alice, room, MatchCommand.UseAbility, new { abilityId = "shield" }));

        Assert.Equal(40, (await ViewAsync(bob, code)).GetProperty("state").GetProperty("shields").GetProperty("1").GetInt32());
        Assert.False(used.GetProperty("replayed").GetBoolean());
    }

    [PostgresFact]
    public async Task Locked_ability_the_game_no_longer_has_is_unavailable()
    {
        var (code, alice, bob) = await ShieldAndStealAsync();

        await RedeployAsync(Catalog(2, shield: null), alice, bob);
        var room = await ViewAsync(alice, code);
        var before = await StoredAsync(code);

        await RefusedAsync(
            CommandAsync(alice, room, MatchCommand.UseAbility, new { abilityId = "shield" }),
            HttpStatusCode.Conflict, MatchCommandEndpoints.ErrorCodes.CommandRejected, AbilityRefusal.Unavailable);
        await AssertUnchangedAsync(code, before, executions: 0);
    }

    [PostgresFact]
    public async Task Game_without_abilities_has_no_such_command()
    {
        var alice = await GuestAsync("Alice");
        var bob = await GuestAsync("Bob");
        var code = await StartedAsync("chog-race", (alice, null), (bob, null));
        var room = await ViewAsync(alice, code);
        var before = await StoredAsync(code);

        await RefusedAsync(
            CommandAsync(alice, room, MatchCommand.UseAbility, new { abilityId = "shield" }),
            HttpStatusCode.BadRequest, MatchCommandEndpoints.ErrorCodes.UnknownCommand);

        Assert.Equal(JsonValueKind.Null, Member(room, "Alice").GetProperty("ability").ValueKind);
        await AssertUnchangedAsync(code, before, executions: 0);
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

    /// <summary>Chog Life's stand-in rules: <c>END_TURN</c> passes the turn; every player sees the whole state.</summary>
    private sealed class TurnRules : IMatchRules
    {
        public string GameType => "chog-life";

        public MatchRuleDecision Apply(MatchCommandContext context) => context.Type switch
        {
            "END_TURN" => MatchRuleDecision.Accept(context.State ?? JsonSerializer.SerializeToElement(new { }), endsTurn: true),
            _ => MatchRuleDecision.Unknown,
        };

        public JsonElement? TimeOutTurn(MatchTurnContext context) => null;

        public JsonElement? ViewFor(MatchViewContext context) => context.State;
    }

    /// <summary>Abilities may be used at any time of the player's own turn.</summary>
    private sealed class OwnTurn : IAbilityTiming
    {
        public string? CurrentWindow(AbilityContext context) => "own_turn";
    }

    private static JsonObject StateOf(AbilityContext context) =>
        context.State is { } state ? JsonNode.Parse(state.GetRawText())!.AsObject() : [];

    private static JsonObject ShieldsOf(JsonObject state) =>
        state["shields"] as JsonObject ?? (JsonObject)(state["shields"] = new JsonObject());

    /// <summary>Shields the user's seat with the configured amount, so opponents can't steal from them.</summary>
    private sealed class ShieldHandler : IAbilityHandler
    {
        private int _executions;

        public int Executions => _executions;

        public string AbilityId => "shield";

        public IReadOnlyList<AbilityParameter> Parameters { get; } = [new("amount", 1, 100)];

        public string? CanExecute(AbilityContext context) => null;

        public JsonElement Execute(AbilityContext context)
        {
            Interlocked.Increment(ref _executions);
            var state = StateOf(context);
            var seat = context.Players.Single(p => p.PlayerId == context.PlayerId).Seat;
            ShieldsOf(state)[seat.ToString()] = (int)context.Ability.Parameters["amount"];
            return JsonSerializer.SerializeToElement(state);
        }
    }

    /// <summary>Steals one of three cards, drawn by the server, from an unshielded opponent.</summary>
    private sealed class StealHandler : IAbilityHandler
    {
        private int _executions;

        public int Executions => _executions;

        public string AbilityId => "steal";

        public IReadOnlyList<AbilityParameter> Parameters { get; } = [];

        public string? CanExecute(AbilityContext context) =>
            ShieldsOf(StateOf(context)).ContainsKey(context.Target!.Seat.ToString()) ? "target_shielded" : null;

        public JsonElement Execute(AbilityContext context)
        {
            Interlocked.Increment(ref _executions);
            var state = StateOf(context);
            var stolen = state["stolen"] as JsonArray ?? (JsonArray)(state["stolen"] = new JsonArray());
            stolen.Add(context.Random.Next(0, 3));
            return JsonSerializer.SerializeToElement(state);
        }
    }
}
