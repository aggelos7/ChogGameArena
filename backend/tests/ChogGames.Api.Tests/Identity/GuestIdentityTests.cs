using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ChogGames.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ChogGames.Api.Tests.Identity;

public sealed class GuestIdentityTests(MigratedPostgresDatabase database) : IClassFixture<MigratedPostgresDatabase>, IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    // Starts at the real time (the HTTP client's cookie jar discards cookies whose expiry has passed in
    // real time), truncated to whole seconds because PostgreSQL stores microseconds, not .NET ticks.
    private readonly ManualTimeProvider _clock = new(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
    private ApiFactory _factory = null!;

    private sealed record PlayerDto(Guid PlayerId, string DisplayName, string Kind);

    public async Task InitializeAsync()
    {
        if (database.ConnectionString.Length > 0)
        {
            await using var db = database.CreateDbContext();
            await db.Database.ExecuteSqlRawAsync("TRUNCATE \"GuestSessions\", \"Players\"");
        }

        _factory = CreateFactory();
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    private ApiFactory CreateFactory(IReadOnlyDictionary<string, string?>? settings = null) =>
        new(database.ConnectionString, settings, services => services.AddSingleton<TimeProvider>(_clock));

    private static async Task<PlayerDto> ReadPlayer(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<PlayerDto>(Json))!;

    private static Task<HttpResponseMessage> CreateGuest(HttpClient client, object? body = null) =>
        client.PostAsJsonAsync("/api/guest-sessions", body ?? new { });

    private static string SessionCookie(HttpResponseMessage response) =>
        response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("chog_session=", StringComparison.Ordinal));

    private static string TokenFrom(string setCookie) => setCookie.Split(';')[0]["chog_session=".Length..];

    private static HttpRequestMessage MeRequest(string? cookieToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/me");
        if (cookieToken is not null)
        {
            request.Headers.Add("Cookie", $"chog_session={cookieToken}");
        }

        return request;
    }

    private async Task<int> CountPlayersAsync()
    {
        await using var db = database.CreateDbContext();
        return await db.Players.CountAsync();
    }

    [PostgresFact]
    public async Task Creating_a_guest_issues_a_secure_http_only_session_cookie()
    {
        using var client = _factory.CreateHttpsClient();

        using var response = await CreateGuest(client);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var player = await ReadPlayer(response);
        Assert.Equal("Guest", player.Kind);
        Assert.Matches("^Chog [0-9]{4}$", player.DisplayName);

        var cookie = SessionCookie(response).ToLowerInvariant();
        Assert.Contains("httponly", cookie);
        Assert.Contains("secure", cookie);
        Assert.Contains("samesite=lax", cookie);
        Assert.Contains("path=/", cookie);
        Assert.DoesNotContain(player.PlayerId.ToString(), cookie);
    }

    [PostgresFact]
    public async Task Refreshing_retains_the_guest_identity()
    {
        using var client = _factory.CreateHttpsClient();
        var created = await ReadPlayer(await CreateGuest(client, new { displayName = "Speedy Chog" }));

        var first = await client.GetFromJsonAsync<PlayerDto>("/api/me", Json);
        var second = await client.GetFromJsonAsync<PlayerDto>("/api/me", Json);

        Assert.Equal(created, first);
        Assert.Equal(created, second);
        Assert.Equal("Speedy Chog", first!.DisplayName);
    }

    [PostgresFact]
    public async Task Creating_again_with_a_valid_session_returns_the_same_guest()
    {
        using var client = _factory.CreateHttpsClient();
        var created = await ReadPlayer(await CreateGuest(client));

        using var again = await CreateGuest(client, new { displayName = "Other Name" });

        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(created, await ReadPlayer(again));
        Assert.Equal(1, await CountPlayersAsync());
    }

    [PostgresFact]
    public async Task Requests_without_a_session_are_unauthorized()
    {
        using var client = _factory.CreateHttpsClient();

        using var response = await client.GetAsync("/api/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [PostgresTheory]
    [InlineData("not-a-token")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("")]
    public async Task Forged_session_tokens_are_rejected(string token)
    {
        using var client = _factory.CreateHttpsClient();
        await CreateGuest(client); // a real session exists, but not for this token

        using var anonymous = _factory.CreateHttpsClient(); // fresh cookie jar
        using var response = await anonymous.SendAsync(MeRequest(token));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [PostgresFact]
    public async Task Supplying_another_guests_id_does_not_change_identity()
    {
        using var alice = _factory.CreateHttpsClient();
        using var bob = _factory.CreateHttpsClient();
        var aliceId = (await ReadPlayer(await CreateGuest(alice, new { displayName = "Alice" }))).PlayerId;
        var bobPlayer = await ReadPlayer(await CreateGuest(bob, new { displayName = "Bob" }));

        // Bob tries every client-controlled channel to claim Alice's id.
        using var meRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/me?playerId={aliceId}");
        meRequest.Headers.Add("X-Player-Id", aliceId.ToString());
        var me = await ReadPlayer(await bob.SendAsync(meRequest));
        var recreated = await ReadPlayer(await CreateGuest(bob, new { playerId = aliceId, displayName = "Alice" }));

        Assert.Equal(bobPlayer, me);
        Assert.Equal(bobPlayer, recreated);

        // Alice's id is not a session token either.
        using var anonymous = _factory.CreateHttpsClient();
        using var asCookie = await anonymous.SendAsync(MeRequest(aliceId.ToString()));
        Assert.Equal(HttpStatusCode.Unauthorized, asCookie.StatusCode);
    }

    [PostgresTheory]
    [InlineData("ab")]
    [InlineData("this name is far too long")]
    [InlineData("<script>")]
    [InlineData("two  spaces")]
    [InlineData("   ")]
    public async Task Invalid_display_name_is_rejected_without_creating_anything(string displayName)
    {
        using var client = _factory.CreateHttpsClient();

        using var response = await CreateGuest(client, new { displayName });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));
        Assert.Contains("displayName", await response.Content.ReadAsStringAsync());
        Assert.Equal(0, await CountPlayersAsync());
    }

    [PostgresFact]
    public async Task Display_name_is_trimmed()
    {
        using var client = _factory.CreateHttpsClient();

        var player = await ReadPlayer(await CreateGuest(client, new { displayName = "  Chog Racer  " }));

        Assert.Equal("Chog Racer", player.DisplayName);
    }

    [PostgresFact]
    public async Task Only_a_hash_of_the_session_token_is_stored()
    {
        using var client = _factory.CreateHttpsClient();
        var token = TokenFrom(SessionCookie(await CreateGuest(client)));

        await using var db = database.CreateDbContext();
        var session = await db.GuestSessions.SingleAsync();

        Assert.Equal(SHA256.HashData(Encoding.ASCII.GetBytes(token)), session.TokenHash);
        Assert.NotEqual(Encoding.ASCII.GetBytes(token), session.TokenHash);
    }

    [PostgresFact]
    public async Task Expired_sessions_are_rejected_and_a_new_guest_can_be_created()
    {
        using var client = _factory.CreateHttpsClient();
        var original = await ReadPlayer(await CreateGuest(client));

        _clock.Advance(TimeSpan.FromDays(91));

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/me")).StatusCode);
        using var recreated = await CreateGuest(client);
        Assert.Equal(HttpStatusCode.Created, recreated.StatusCode);
        Assert.NotEqual(original.PlayerId, (await ReadPlayer(recreated)).PlayerId);
    }

    [PostgresFact]
    public async Task Revoked_sessions_are_rejected()
    {
        using var client = _factory.CreateHttpsClient();
        await CreateGuest(client);

        await using (var db = database.CreateDbContext())
        {
            var session = await db.GuestSessions.SingleAsync();
            session.Revoke(_clock.GetUtcNow());
            await db.SaveChangesAsync();
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/me")).StatusCode);
    }

    [PostgresFact]
    public async Task Using_a_session_extends_it_at_most_once_per_day()
    {
        using var client = _factory.CreateHttpsClient();
        await CreateGuest(client);
        var createdAt = _clock.Now;

        _clock.Advance(TimeSpan.FromHours(12));
        using var early = await client.GetAsync("/api/me");
        Assert.False(early.Headers.Contains("Set-Cookie"));

        _clock.Advance(TimeSpan.FromDays(80)); // still within the original 90 days
        using var renewed = await client.GetAsync("/api/me");
        Assert.Equal(HttpStatusCode.OK, renewed.StatusCode);
        Assert.Contains("chog_session=", SessionCookie(renewed));

        await using var db = database.CreateDbContext();
        var session = await db.GuestSessions.SingleAsync();
        Assert.Equal(_clock.Now + TimeSpan.FromDays(90), session.ExpiresAt);

        _clock.Advance(TimeSpan.FromDays(30)); // past the original expiry, within the renewed one
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/me")).StatusCode);
        Assert.True(_clock.Now > createdAt + TimeSpan.FromDays(90));
    }

    [PostgresFact]
    public async Task Guest_creation_is_rate_limited_per_client()
    {
        await using var factory = CreateFactory(new Dictionary<string, string?> { ["GuestSessions:CreatePerMinutePerClient"] = "2" });

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 3; i++)
        {
            using var client = factory.CreateHttpsClient(); // new cookie jar each time: a new guest per request
            statuses.Add((await CreateGuest(client)).StatusCode);
        }

        Assert.Equal([HttpStatusCode.Created, HttpStatusCode.Created, HttpStatusCode.TooManyRequests], statuses);
        Assert.Equal(2, await CountPlayersAsync());
    }
}
