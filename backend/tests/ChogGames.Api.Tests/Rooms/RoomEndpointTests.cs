using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ChogGames.Core.Rooms;
using Microsoft.EntityFrameworkCore;

namespace ChogGames.Api.Tests.Rooms;

public sealed class RoomEndpointTests(MigratedPostgresDatabase database) : IClassFixture<MigratedPostgresDatabase>, IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private ApiFactory _factory = null!;

    private static readonly CharacterDto Classic = new("Standard", "classic", "Classic", "#5b2bd6");

    private sealed record CharacterDto(string Kind, string Id, string Name, string? Color);

    private sealed record MemberDto(
        string DisplayName, string AvatarId, bool IsHost, bool IsYou, bool IsReady, CharacterDto Character, string? Ability, bool HasLeft);

    private sealed record RoomDto(string Code, string GameType, string Status, int MinPlayers, int Capacity, MemberDto[] Members);

    private sealed record ProblemDto(int Status, string? Code, string? RoomCode, string? Detail);

    public async Task InitializeAsync()
    {
        if (database.ConnectionString.Length > 0)
        {
            await using var db = database.CreateDbContext();
            await db.Database.ExecuteSqlRawAsync("TRUNCATE \"GuestSessions\", \"Players\" CASCADE");
        }

        _factory = CreateFactory();
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    // Chog Life enabled, Chog Race left as coming soon.
    private ApiFactory CreateFactory(IReadOnlyDictionary<string, string?>? extra = null)
    {
        var settings = new Dictionary<string, string?> { ["Games:Items:0:Status"] = "Available" };
        foreach (var (key, value) in extra ?? new Dictionary<string, string?>())
        {
            settings[key] = value;
        }

        return new ApiFactory(database.ConnectionString, settings);
    }

    private async Task<HttpClient> GuestAsync(string name, ApiFactory? factory = null)
    {
        var client = (factory ?? _factory).CreateHttpsClient();
        using var response = await client.PostAsJsonAsync("/api/guest-sessions", new { displayName = name });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return client;
    }

    private static Task<HttpResponseMessage> CreateAsync(HttpClient client, object? body) => client.PostAsJsonAsync("/api/rooms", body);

    private static Task<HttpResponseMessage> JoinAsync(HttpClient client, string code) => client.PostAsync($"/api/rooms/{code}/join", null);

    private static async Task<RoomDto> CreatedRoomAsync(HttpClient client)
    {
        using var response = await CreateAsync(client, new { gameType = "chog-life" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<RoomDto>(Json))!;
    }

    private static async Task<RoomDto> RoomAsync(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        Assert.Equal(expected, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<RoomDto>(Json))!;
    }

    private static async Task<ProblemDto> ProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = (await response.Content.ReadFromJsonAsync<ProblemDto>(Json))!;
        Assert.Equal(((int)status, code), (problem.Status, problem.Code));
        return problem;
    }

    private async Task SetStatusAsync(string code, string status)
    {
        await using var db = database.CreateDbContext();
        await RoomStatusOverride.SetAsync(db, code, Enum.Parse<RoomStatus>(status), DateTimeOffset.UtcNow);
    }

    [PostgresFact]
    public async Task Requests_without_a_session_are_unauthorized()
    {
        using var client = _factory.CreateHttpsClient();

        using var create = await CreateAsync(client, new { gameType = "chog-life" });
        using var join = await JoinAsync(client, "K7QF3M");
        using var get = await client.GetAsync("/api/rooms/K7QF3M");
        using var ready = await client.PutAsJsonAsync("/api/rooms/K7QF3M/ready", new { ready = true });
        using var start = await client.PostAsync("/api/rooms/K7QF3M/start", null);
        using var leave = await client.PostAsync("/api/rooms/K7QF3M/leave", null);

        Assert.All(
            new[] { create.StatusCode, join.StatusCode, get.StatusCode, ready.StatusCode, start.StatusCode, leave.StatusCode },
            status => Assert.Equal(HttpStatusCode.Unauthorized, status));
    }

    [PostgresFact]
    public async Task Creating_a_room_returns_its_code_with_the_caller_as_host()
    {
        using var alice = await GuestAsync("Alice");

        using var response = await CreateAsync(alice, new { gameType = "chog-life" });

        var room = await RoomAsync(response, HttpStatusCode.Created);
        Assert.Matches("^[2-9A-HJKMNP-Z]{6}$", room.Code);
        Assert.Equal($"/api/rooms/{room.Code}", response.Headers.Location?.OriginalString);
        Assert.Equal(("chog-life", "Waiting", 2, 6), (room.GameType, room.Status, room.MinPlayers, room.Capacity));
        Assert.Equal([new MemberDto("Alice", "classic", IsHost: true, IsYou: true, IsReady: false, Classic, Ability: null, HasLeft: false)], room.Members);
    }

    [PostgresFact]
    public async Task Response_never_exposes_player_ids()
    {
        using var alice = await GuestAsync("Alice");
        var me = await alice.GetFromJsonAsync<JsonElement>("/api/me");

        using var response = await CreateAsync(alice, new { gameType = "chog-life" });
        var json = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain(me.GetProperty("playerId").GetString()!, json);
        Assert.DoesNotContain("playerId", json, StringComparison.OrdinalIgnoreCase);
    }

    [PostgresFact]
    public async Task Host_comes_from_the_session_not_the_request()
    {
        using var alice = await GuestAsync("Alice");
        using var bob = await GuestAsync("Bob");
        var bobId = (await bob.GetFromJsonAsync<JsonElement>("/api/me")).GetProperty("playerId").GetString();

        using var response = await CreateAsync(alice, new { gameType = "chog-life", hostPlayerId = bobId, playerId = bobId });

        var room = await RoomAsync(response, HttpStatusCode.Created);
        Assert.Equal("Alice", Assert.Single(room.Members).DisplayName);
        await ProblemAsync(await bob.GetAsync($"/api/rooms/{room.Code}"), HttpStatusCode.NotFound, "room_not_found");
    }

    [PostgresTheory]
    [InlineData("{\"gameType\":\"chog-unknown\"}")]
    [InlineData("{\"gameType\":\"CHOG-LIFE\"}")]
    [InlineData("{\"gameType\":\"\"}")]
    [InlineData("{}")]
    public async Task Unknown_game_is_a_bad_request(string body)
    {
        using var alice = await GuestAsync("Alice");

        using var response = await alice.PostAsync("/api/rooms", new StringContent(body, System.Text.Encoding.UTF8, "application/json"));

        await ProblemAsync(response, HttpStatusCode.BadRequest, "unknown_game");
    }

    [PostgresFact]
    public async Task Game_that_is_not_available_is_a_conflict()
    {
        using var alice = await GuestAsync("Alice");

        using var response = await CreateAsync(alice, new { gameType = "chog-race" });

        await ProblemAsync(response, HttpStatusCode.Conflict, "game_unavailable");
    }

    [PostgresFact]
    public async Task Second_room_is_refused_with_the_open_room_code()
    {
        using var alice = await GuestAsync("Alice");
        var first = await CreatedRoomAsync(alice);

        using var response = await CreateAsync(alice, new { gameType = "chog-life" });

        var problem = await ProblemAsync(response, HttpStatusCode.Conflict, "already_in_room");
        Assert.Equal(first.Code, problem.RoomCode);
    }

    [PostgresFact]
    public async Task Second_player_joins_by_code_and_both_see_the_same_room()
    {
        using var alice = await GuestAsync("Alice");
        using var bob = await GuestAsync("Bob");
        var created = await CreatedRoomAsync(alice);

        var joined = await RoomAsync(await JoinAsync(bob, created.Code.ToLowerInvariant()));
        var seenByHost = await alice.GetFromJsonAsync<RoomDto>($"/api/rooms/{created.Code}", Json);

        Assert.Equal(created.Code, joined.Code);
        Assert.Equal(
            [
                new MemberDto("Alice", "classic", IsHost: true, IsYou: false, IsReady: false, Classic, Ability: null, HasLeft: false),
                new MemberDto("Bob", "classic", IsHost: false, IsYou: true, IsReady: false, Classic, Ability: null, HasLeft: false),
            ],
            joined.Members);
        Assert.Equal([true, false], seenByHost!.Members.Select(m => m.IsYou));
        Assert.Equal(joined.Members.Select(m => m.DisplayName), seenByHost.Members.Select(m => m.DisplayName));
    }

    [PostgresFact]
    public async Task Joining_again_returns_the_room_unchanged()
    {
        using var alice = await GuestAsync("Alice");
        using var bob = await GuestAsync("Bob");
        var created = await CreatedRoomAsync(alice);
        (await JoinAsync(bob, created.Code)).Dispose();

        var again = await RoomAsync(await JoinAsync(bob, created.Code));
        var hostJoins = await RoomAsync(await JoinAsync(alice, created.Code));

        Assert.Equal(2, again.Members.Length);
        Assert.Equal(2, hostJoins.Members.Length);
    }

    [PostgresTheory]
    [InlineData("K7QF3")]
    [InlineData("K7QF0M")]
    [InlineData("not-a-code")]
    [InlineData("ZZZZZZ")]
    public async Task Malformed_or_unknown_code_is_not_found(string code)
    {
        using var bob = await GuestAsync("Bob");

        await ProblemAsync(await JoinAsync(bob, code), HttpStatusCode.NotFound, "room_not_found");
        await ProblemAsync(await bob.GetAsync($"/api/rooms/{code}"), HttpStatusCode.NotFound, "room_not_found");
    }

    [PostgresFact]
    public async Task Full_room_is_refused()
    {
        using var host = await GuestAsync("Host");
        var room = await CreatedRoomAsync(host);
        for (var i = 1; i <= 5; i++)
        {
            using var player = await GuestAsync($"Player {i}");
            Assert.Equal(i + 1, (await RoomAsync(await JoinAsync(player, room.Code))).Members.Length);
        }

        using var seventh = await GuestAsync("Seventh");

        await ProblemAsync(await JoinAsync(seventh, room.Code), HttpStatusCode.Conflict, "room_full");
        Assert.Equal(6, (await host.GetFromJsonAsync<RoomDto>($"/api/rooms/{room.Code}", Json))!.Members.Length);
    }

    [PostgresTheory]
    [InlineData("Active", "room_started")]
    [InlineData("Completed", "room_closed")]
    [InlineData("Cancelled", "room_closed")]
    [InlineData("Abandoned", "room_closed")]
    public async Task Room_that_is_no_longer_waiting_is_refused(string status, string code)
    {
        using var alice = await GuestAsync("Alice");
        using var bob = await GuestAsync("Bob");
        var room = await CreatedRoomAsync(alice);
        await SetStatusAsync(room.Code, status);

        await ProblemAsync(await JoinAsync(bob, room.Code), HttpStatusCode.Conflict, code);
    }

    [PostgresFact]
    public async Task Player_in_another_room_is_refused_with_that_room_code()
    {
        using var alice = await GuestAsync("Alice");
        using var carol = await GuestAsync("Carol");
        using var bob = await GuestAsync("Bob");
        var first = await CreatedRoomAsync(alice);
        var second = await CreatedRoomAsync(carol);
        (await JoinAsync(bob, first.Code)).Dispose();

        var problem = await ProblemAsync(await JoinAsync(bob, second.Code), HttpStatusCode.Conflict, "already_in_room");

        Assert.Equal(first.Code, problem.RoomCode);
        Assert.Single((await carol.GetFromJsonAsync<RoomDto>($"/api/rooms/{second.Code}", Json))!.Members);
    }

    [PostgresFact]
    public async Task Non_member_cannot_read_a_room()
    {
        using var alice = await GuestAsync("Alice");
        using var mallory = await GuestAsync("Mallory");
        var room = await CreatedRoomAsync(alice);

        await ProblemAsync(await mallory.GetAsync($"/api/rooms/{room.Code}"), HttpStatusCode.NotFound, "room_not_found");
    }

    [PostgresFact]
    public async Task Requests_are_rate_limited_per_player()
    {
        await using var factory = CreateFactory(new Dictionary<string, string?> { ["Rooms:RequestsPerMinutePerPlayer"] = "2" });
        using var alice = await GuestAsync("Alice", factory);
        using var bob = await GuestAsync("Bob", factory);

        (await alice.GetAsync("/api/rooms/K7QF3M")).Dispose();
        (await alice.GetAsync("/api/rooms/K7QF3M")).Dispose();
        using var limited = await JoinAsync(alice, "K7QF3M");
        using var other = await bob.GetAsync("/api/rooms/K7QF3M");

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, other.StatusCode);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("1001")]
    public void Invalid_rate_limit_setting_fails_at_startup(string value)
    {
        var error = StartupValidation.Failure(new Dictionary<string, string?> { ["Rooms:RequestsPerMinutePerPlayer"] = value });

        Assert.Contains("Rooms:RequestsPerMinutePerPlayer", error.Message);
    }

    [Fact]
    public void Valid_settings_pass_startup_validation() =>
        StartupValidation.Validate(new Dictionary<string, string?> { ["Rooms:RequestsPerMinutePerPlayer"] = "1000" });
}
