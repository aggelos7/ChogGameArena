using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace ChogGames.Api.Tests.Profile;

public sealed class ProfileTests(MigratedPostgresDatabase database) : IClassFixture<MigratedPostgresDatabase>, IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private ApiFactory _factory = null!;

    private sealed record AvatarDto(string Id, string Name, string Color);

    private sealed record ProfileDto(Guid PlayerId, string DisplayName, string Kind, AvatarDto Avatar, int GamesPlayed, int Wins);

    public async Task InitializeAsync()
    {
        if (database.ConnectionString.Length > 0)
        {
            await using var db = database.CreateDbContext();
            await db.Database.ExecuteSqlRawAsync("TRUNCATE \"GuestSessions\", \"Players\" CASCADE");
        }

        _factory = new ApiFactory(database.ConnectionString);
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    private async Task<HttpClient> GuestAsync(string displayName)
    {
        var client = _factory.CreateHttpsClient();
        using var response = await client.PostAsJsonAsync("/api/guest-sessions", new { displayName });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return client;
    }

    private static async Task<ProfileDto> GetProfileAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<ProfileDto>("/api/profile", Json))!;

    private static Task<HttpResponseMessage> PatchAsync(HttpClient client, object body) =>
        client.PatchAsJsonAsync("/api/profile", body);

    [PostgresFact]
    public async Task New_guest_profile_has_default_avatar_and_zero_counters()
    {
        using var client = await GuestAsync("Alice");

        var profile = await GetProfileAsync(client);

        Assert.Equal("Alice", profile.DisplayName);
        Assert.Equal("Guest", profile.Kind);
        Assert.Equal(new AvatarDto("classic", "Classic", "#5b2bd6"), profile.Avatar);
        Assert.Equal(0, profile.GamesPlayed);
        Assert.Equal(0, profile.Wins);
    }

    [PostgresFact]
    public async Task Avatar_options_are_listed()
    {
        using var client = await GuestAsync("Alice");

        var avatars = await client.GetFromJsonAsync<AvatarDto[]>("/api/profile/avatars", Json);

        Assert.Equal(["classic", "mint", "sunset", "ocean", "berry", "gold"], avatars!.Select(a => a.Id));
        Assert.All(avatars!, a => Assert.Matches("^#[0-9a-f]{6}$", a.Color));
    }

    [PostgresFact]
    public async Task Owner_can_update_display_name_and_avatar_and_changes_persist()
    {
        using var client = await GuestAsync("Alice");

        using var response = await PatchAsync(client, new { displayName = "  Alice Racer ", avatarId = "ocean" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = (await response.Content.ReadFromJsonAsync<ProfileDto>(Json))!;
        Assert.Equal("Alice Racer", updated.DisplayName);
        Assert.Equal("ocean", updated.Avatar.Id);

        var reloaded = await GetProfileAsync(client);
        Assert.Equal(updated, reloaded);
        Assert.Contains("\"displayName\":\"Alice Racer\"", await client.GetStringAsync("/api/me"));
    }

    [PostgresFact]
    public async Task Fields_can_be_updated_independently()
    {
        using var client = await GuestAsync("Alice");

        await PatchAsync(client, new { avatarId = "mint" });
        await PatchAsync(client, new { displayName = "Alice Two" });

        var profile = await GetProfileAsync(client);
        Assert.Equal("Alice Two", profile.DisplayName);
        Assert.Equal("mint", profile.Avatar.Id);
    }

    [PostgresTheory]
    [InlineData("ab")]
    [InlineData("this name is far too long")]
    [InlineData("<b>bold</b>")]
    [InlineData("two  spaces")]
    [InlineData("")]
    public async Task Invalid_display_name_is_rejected_without_any_change(string displayName)
    {
        using var client = await GuestAsync("Alice");

        using var response = await PatchAsync(client, new { displayName, avatarId = "ocean" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("displayName", await response.Content.ReadAsStringAsync());
        var profile = await GetProfileAsync(client);
        Assert.Equal("Alice", profile.DisplayName);
        Assert.Equal("classic", profile.Avatar.Id); // the valid avatar in the same request was not applied
    }

    [PostgresTheory]
    [InlineData("unknown")]
    [InlineData("Classic")]
    [InlineData("")]
    public async Task Unknown_avatar_is_rejected_without_any_change(string avatarId)
    {
        using var client = await GuestAsync("Alice");

        using var response = await PatchAsync(client, new { displayName = "Alice Two", avatarId });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("avatarId", await response.Content.ReadAsStringAsync());
        Assert.Equal("Alice", (await GetProfileAsync(client)).DisplayName);
    }

    [PostgresFact]
    public async Task Request_with_no_editable_fields_is_rejected()
    {
        using var client = await GuestAsync("Alice");

        using var response = await PatchAsync(client, new { gamesPlayed = 99, wins = 99 });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var profile = await GetProfileAsync(client);
        Assert.Equal((0, 0), (profile.GamesPlayed, profile.Wins));
    }

    [PostgresFact]
    public async Task Counters_cannot_be_set_by_the_client()
    {
        using var client = await GuestAsync("Alice");

        using var response = await PatchAsync(client, new { displayName = "Alice Two", gamesPlayed = 99, wins = 42 });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var profile = await GetProfileAsync(client);
        Assert.Equal("Alice Two", profile.DisplayName);
        Assert.Equal((0, 0), (profile.GamesPlayed, profile.Wins));
    }

    [PostgresFact]
    public async Task Counters_are_read_from_the_server_record()
    {
        using var client = await GuestAsync("Alice");
        var playerId = (await GetProfileAsync(client)).PlayerId;
        await using (var db = database.CreateDbContext())
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"Players\" SET \"GamesPlayed\" = 5, \"Wins\" = 2 WHERE \"Id\" = {playerId}");
        }

        var profile = await GetProfileAsync(client);

        Assert.Equal((5, 2), (profile.GamesPlayed, profile.Wins));
    }

    [PostgresFact]
    public async Task A_player_can_only_update_their_own_profile()
    {
        using var alice = await GuestAsync("Alice");
        using var bob = await GuestAsync("Bob");
        var aliceProfile = await GetProfileAsync(alice);

        // Bob names Alice's id in the body, query string and a header.
        using var request = new HttpRequestMessage(HttpMethod.Patch, $"/api/profile?playerId={aliceProfile.PlayerId}")
        {
            Content = JsonContent.Create(new { playerId = aliceProfile.PlayerId, displayName = "Hacked", avatarId = "gold" }),
        };
        request.Headers.Add("X-Player-Id", aliceProfile.PlayerId.ToString());
        using var response = await bob.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(aliceProfile, await GetProfileAsync(alice));
        var bobProfile = await GetProfileAsync(bob);
        Assert.Equal("Hacked", bobProfile.DisplayName); // only Bob's own profile changed
        Assert.NotEqual(aliceProfile.PlayerId, bobProfile.PlayerId);
    }

    [PostgresFact]
    public async Task Profile_requires_a_session()
    {
        using var alice = await GuestAsync("Alice");
        using var anonymous = _factory.CreateHttpsClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/profile")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/profile/avatars")).StatusCode);
        using var patch = await PatchAsync(anonymous, new { displayName = "Hacked" });
        Assert.Equal(HttpStatusCode.Unauthorized, patch.StatusCode);

        await using var db = database.CreateDbContext();
        Assert.DoesNotContain(await db.Players.Select(p => p.DisplayName).ToListAsync(), name => name == "Hacked");
    }

    [PostgresFact]
    public async Task Database_rejects_inconsistent_counters()
    {
        using var client = await GuestAsync("Alice");
        var playerId = (await GetProfileAsync(client)).PlayerId;

        await using var db = database.CreateDbContext();
        await Assert.ThrowsAnyAsync<Exception>(() =>
            db.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"Players\" SET \"GamesPlayed\" = 1, \"Wins\" = 2 WHERE \"Id\" = {playerId}"));
    }
}
