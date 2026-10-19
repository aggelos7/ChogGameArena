using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ChogGames.Core.Players;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using SkiaSharp;

namespace ChogGames.Api.Tests.Profile;

public sealed class AvatarImageTests(MigratedPostgresDatabase database) : IClassFixture<MigratedPostgresDatabase>, IAsyncLifetime
{
    private const string Upload = "/api/profile/avatar-image";
    private const int MaxUploadBytes = 200_000;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly List<ApiFactory> _factories = [];
    private ApiFactory _factory = null!;

    private sealed record AvatarDto(string Id, string Name, string Color, string? ImageUrl);

    private sealed record ProfileDto(Guid PlayerId, string DisplayName, AvatarDto Avatar);

    private sealed record ProblemDto(int Status, string? Code);

    public async Task InitializeAsync()
    {
        if (database.ConnectionString.Length > 0)
        {
            await using var db = database.CreateDbContext();
            await db.Database.ExecuteSqlRawAsync("TRUNCATE \"GuestSessions\", \"Players\" CASCADE");
        }

        _factory = Factory();
    }

    public async Task DisposeAsync()
    {
        foreach (var factory in _factories)
        {
            await factory.DisposeAsync();
        }
    }

    private ApiFactory Factory(int changesPerWindow = 100)
    {
        var factory = new ApiFactory(database.ConnectionString, new Dictionary<string, string?>
        {
            ["AvatarImages:MaxUploadBytes"] = MaxUploadBytes.ToString(),
            ["AvatarImages:MaxSourceDimension"] = "2000",
            ["AvatarImages:ChangesPerWindowPerPlayer"] = changesPerWindow.ToString(),
            ["GuestSessions:CreatePerMinutePerClient"] = "1000",
            ["Games:Items:0:Status"] = "Available",
        });
        _factories.Add(factory);
        return factory;
    }

    /// <summary>A guest who accepted the terms, as uploading needs (DEC-070); <see cref="TermsTests"/> covers refusals.</summary>
    private async Task<HttpClient> GuestAsync(string displayName, ApiFactory? factory = null)
    {
        var client = (factory ?? _factory).CreateHttpsClient();
        using var response = await client.PostAsJsonAsync("/api/guest-sessions", new { displayName });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var accepted = await client.PostAsJsonAsync("/api/profile/terms", new { version = TermsOfUse.CurrentVersion });
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        return client;
    }

    private static byte[] Image(int width, int height, SKEncodedImageFormat format = SKEncodedImageFormat.Jpeg)
    {
        using var surface = SKSurface.Create(new SKImageInfo(width, height));
        surface.Canvas.Clear(SKColors.Teal);
        using var image = surface.Snapshot();
        using var data = image.Encode(format, 90);
        return data.ToArray();
    }

    private static async Task<HttpResponseMessage> PutAsync(HttpClient client, byte[] body, string contentType = "image/jpeg", string path = Upload)
    {
        var content = new ByteArrayContent(body);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        return await client.PutAsync(path, content);
    }

    private static async Task<ProfileDto> UploadedAsync(HttpClient client, byte[] body, string contentType = "image/jpeg")
    {
        using var response = await PutAsync(client, body, contentType);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ProfileDto>(Json))!;
    }

    private static async Task<ProfileDto> ProfileAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<ProfileDto>("/api/profile", Json))!;

    private static async Task AssertRefusedAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(code, (await response.Content.ReadFromJsonAsync<ProblemDto>(Json))!.Code);
    }

    private async Task<(Guid Id, Guid PlayerId, int Bytes)[]> StoredAsync()
    {
        await using var db = database.CreateDbContext();
        return [.. (await db.AvatarImages.AsNoTracking().Select(i => new { i.Id, i.PlayerId, i.Data.Length }).ToListAsync())
            .Select(i => (i.Id, i.PlayerId, i.Length))];
    }

    [PostgresTheory]
    [InlineData(SKEncodedImageFormat.Jpeg, "image/jpeg")]
    [InlineData(SKEncodedImageFormat.Png, "image/png")]
    [InlineData(SKEncodedImageFormat.Webp, "image/webp")]
    public async Task An_upload_becomes_the_avatar_as_a_square_webp_served_safely(SKEncodedImageFormat format, string contentType)
    {
        using var alice = await GuestAsync("Alice");

        var profile = await UploadedAsync(alice, Image(800, 600, format), contentType);

        var url = profile.Avatar.ImageUrl!;
        Assert.Matches("^/api/avatar-images/[0-9a-f-]{36}$", url);
        Assert.DoesNotContain(profile.PlayerId.ToString(), url);
        Assert.Equal("classic", profile.Avatar.Id); // the built-in avatar stays, for when the photo is removed
        Assert.Equal(profile, await ProfileAsync(alice));

        using var served = await alice.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, served.StatusCode);
        Assert.Equal("image/webp", served.Content.Headers.ContentType!.ToString());
        Assert.Equal("nosniff", served.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.True(served.Headers.CacheControl!.Private);
        Assert.Equal(TimeSpan.FromHours(1), served.Headers.CacheControl.MaxAge);
        Assert.Equal("default-src 'none'; sandbox", served.Headers.GetValues("Content-Security-Policy").Single());
        var webp = await served.Content.ReadAsByteArrayAsync();
        using var codec = SKCodec.Create(SKData.CreateCopy(webp));
        Assert.Equal(SKEncodedImageFormat.Webp, codec.EncodedFormat);
        Assert.Equal((256, 256), (codec.Info.Width, codec.Info.Height));
        Assert.Equal(webp.Length, Assert.Single(await StoredAsync()).Bytes);
    }

    [PostgresFact]
    public async Task Other_players_see_the_photo_and_anonymous_requests_are_refused()
    {
        using var alice = await GuestAsync("Alice");
        using var bob = await GuestAsync("Bob");
        var url = (await UploadedAsync(alice, Image(300, 300))).Avatar.ImageUrl!;

        using var seen = await bob.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, seen.StatusCode);
        Assert.Equal(await alice.GetByteArrayAsync(url), await seen.Content.ReadAsByteArrayAsync());

        using var anonymous = _factory.CreateHttpsClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PutAsync(anonymous, Image(300, 300))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.DeleteAsync(Upload)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.GetAsync($"/api/avatar-images/{Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.GetAsync("/api/avatar-images/not-a-guid")).StatusCode);
    }

    [PostgresFact]
    public async Task Room_members_carry_their_current_photo_for_the_pawns()
    {
        using var alice = await GuestAsync("Alice");
        using var bob = await GuestAsync("Bob");
        var url = (await UploadedAsync(alice, Image(300, 300))).Avatar.ImageUrl!;
        using var created = await alice.PostAsJsonAsync("/api/rooms", new { gameType = "chog-life" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var code = (await created.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("code").GetString();
        (await bob.PostAsync($"/api/rooms/{code}/join", null)).Dispose();

        async Task<Dictionary<string, string?>> PhotosAsync() =>
            (await bob.GetFromJsonAsync<JsonElement>($"/api/rooms/{code}", Json)).GetProperty("members").EnumerateArray()
                .ToDictionary(m => m.GetProperty("displayName").GetString()!, m => m.GetProperty("avatarImageUrl").GetString());

        Assert.Equal(new Dictionary<string, string?> { ["Alice"] = url, ["Bob"] = null }, await PhotosAsync());

        (await alice.DeleteAsync(Upload)).Dispose();
        var bobs = (await UploadedAsync(bob, Image(300, 300))).Avatar.ImageUrl!;

        Assert.Equal(new Dictionary<string, string?> { ["Alice"] = null, ["Bob"] = bobs }, await PhotosAsync());
    }

    [PostgresFact]
    public async Task A_new_upload_replaces_the_old_photo_which_is_deleted()
    {
        using var alice = await GuestAsync("Alice");
        var first = (await UploadedAsync(alice, Image(300, 300))).Avatar.ImageUrl!;

        var second = (await UploadedAsync(alice, Image(400, 300, SKEncodedImageFormat.Png), "image/png")).Avatar.ImageUrl!;

        Assert.NotEqual(first, second);
        Assert.Equal(HttpStatusCode.NotFound, (await alice.GetAsync(first)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await alice.GetAsync(second)).StatusCode);
        Assert.Equal(second, $"/api/avatar-images/{Assert.Single(await StoredAsync()).Id}");
    }

    [PostgresFact]
    public async Task Removing_the_photo_deletes_it_and_shows_the_built_in_avatar_again()
    {
        using var alice = await GuestAsync("Alice");
        using (var changed = await alice.PatchAsJsonAsync("/api/profile", new { avatarId = "ocean" }))
        {
            Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        }

        var url = (await UploadedAsync(alice, Image(300, 300))).Avatar.ImageUrl!;

        using var removed = await alice.DeleteAsync(Upload);

        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        var profile = (await removed.Content.ReadFromJsonAsync<ProfileDto>(Json))!;
        Assert.Equal(new AvatarDto("ocean", "Ocean", "#1d4ed8", null), profile.Avatar);
        Assert.Equal(profile, await ProfileAsync(alice));
        Assert.Equal(HttpStatusCode.NotFound, (await alice.GetAsync(url)).StatusCode);
        Assert.Empty(await StoredAsync());

        using var again = await alice.DeleteAsync(Upload);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
    }

    public static TheoryData<string, byte[], string, HttpStatusCode, string> Refusals() => new()
    {
        { "too big", [.. Image(10, 10), .. new byte[MaxUploadBytes]], "image/jpeg", HttpStatusCode.RequestEntityTooLarge, "avatar_too_large" },
        { "plain text type", Image(300, 300), "text/plain", HttpStatusCode.UnsupportedMediaType, "avatar_unsupported_type" },
        { "no image type", Image(300, 300), "application/octet-stream", HttpStatusCode.UnsupportedMediaType, "avatar_unsupported_type" },
        { "gif type", Image(300, 300), "image/gif", HttpStatusCode.UnsupportedMediaType, "avatar_unsupported_type" },
        { "svg type", """<svg xmlns="http://www.w3.org/2000/svg"/>"""u8.ToArray(), "image/svg+xml", HttpStatusCode.UnsupportedMediaType, "avatar_unsupported_type" },
        { "html as jpeg", "<!doctype html><script>alert(1)</script>"u8.ToArray(), "image/jpeg", HttpStatusCode.BadRequest, "avatar_not_an_image" },
        { "empty", [], "image/png", HttpStatusCode.BadRequest, "avatar_not_an_image" },
        { "truncated", Image(300, 300)[..300], "image/jpeg", HttpStatusCode.BadRequest, "avatar_not_an_image" },
        { "too many pixels", Image(2001, 20, SKEncodedImageFormat.Png), "image/png", HttpStatusCode.BadRequest, "avatar_too_many_pixels" },
    };

    [PostgresTheory]
    [MemberData(nameof(Refusals))]
    public async Task Refused_uploads_leave_the_avatar_unchanged(string name, byte[] body, string contentType, HttpStatusCode status, string code)
    {
        using var alice = await GuestAsync("Alice");
        var before = await UploadedAsync(alice, Image(300, 300));
        var stored = await StoredAsync();

        using var refused = await PutAsync(alice, body, contentType);

        await AssertRefusedAsync(refused, status, code);
        Assert.True(before == await ProfileAsync(alice), name);
        Assert.Equal(stored, await StoredAsync());
        Assert.Equal(HttpStatusCode.OK, (await alice.GetAsync(before.Avatar.ImageUrl)).StatusCode);
    }

    [PostgresFact]
    public async Task A_body_without_a_length_is_cut_off_at_the_limit()
    {
        using var alice = await GuestAsync("Alice");
        var content = new StreamContent(new MemoryStream([.. Image(10, 10), .. new byte[MaxUploadBytes]]));
        content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        using var request = new HttpRequestMessage(HttpMethod.Put, Upload) { Content = content };
        request.Headers.TransferEncodingChunked = true;

        using var refused = await alice.SendAsync(request);

        await AssertRefusedAsync(refused, HttpStatusCode.RequestEntityTooLarge, "avatar_too_large");
        Assert.Empty(await StoredAsync());
    }

    [PostgresFact]
    public async Task No_player_can_change_another_players_photo()
    {
        using var alice = await GuestAsync("Alice");
        using var bob = await GuestAsync("Bob");
        var aliceProfile = await UploadedAsync(alice, Image(300, 300));

        // There is no player in any route or body; a stray id changes nothing but the caller's own photo.
        using (var upload = await PutAsync(bob, Image(300, 300), path: $"{Upload}?playerId={aliceProfile.PlayerId}"))
        {
            Assert.Equal(HttpStatusCode.OK, upload.StatusCode);
        }

        using (var removed = await bob.DeleteAsync($"{Upload}?playerId={aliceProfile.PlayerId}"))
        {
            Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        }

        Assert.Equal(aliceProfile, await ProfileAsync(alice));
        Assert.Equal(aliceProfile.PlayerId, Assert.Single(await StoredAsync()).PlayerId);
        Assert.Null((await ProfileAsync(bob)).Avatar.ImageUrl);
    }

    [PostgresFact]
    public async Task Changes_are_rate_limited_per_player()
    {
        var factory = Factory(changesPerWindow: 2);
        using var alice = await GuestAsync("Alice", factory);
        using var bob = await GuestAsync("Bob", factory);

        Assert.Equal(HttpStatusCode.OK, (await PutAsync(alice, Image(300, 300))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await alice.DeleteAsync(Upload)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await PutAsync(alice, Image(300, 300))).StatusCode);

        Assert.Empty(await StoredAsync());
        Assert.Equal(HttpStatusCode.OK, (await PutAsync(bob, Image(300, 300))).StatusCode);
    }

    [PostgresFact]
    public async Task Simultaneous_uploads_leave_exactly_one_photo()
    {
        using var alice = await GuestAsync("Alice");

        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(i => PutAsync(alice, Image(300 + i, 300))));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        var stored = Assert.Single(await StoredAsync());
        Assert.Equal($"/api/avatar-images/{stored.Id}", (await ProfileAsync(alice)).Avatar.ImageUrl);
        foreach (var response in responses)
        {
            response.Dispose();
        }
    }

    [PostgresFact]
    public async Task An_operator_deleting_the_row_restores_the_built_in_avatar()
    {
        using var alice = await GuestAsync("Alice");
        var profile = await UploadedAsync(alice, Image(300, 300));

        // The runbook's removal (DEC-018): one statement, by player id.
        await using (var db = database.CreateDbContext())
        {
            Assert.Equal(1, await db.Database.ExecuteSqlInterpolatedAsync($"""DELETE FROM "AvatarImages" WHERE "PlayerId" = {profile.PlayerId}"""));
        }

        Assert.Null((await ProfileAsync(alice)).Avatar.ImageUrl);
        Assert.Equal(HttpStatusCode.NotFound, (await alice.GetAsync(profile.Avatar.ImageUrl)).StatusCode);
    }

    [PostgresFact]
    public async Task The_table_refuses_empty_oversized_second_and_orphan_images_and_follows_its_player()
    {
        using var alice = await GuestAsync("Alice");
        var profile = await UploadedAsync(alice, Image(300, 300));
        await using var db = database.CreateDbContext();

        Task InsertAsync(Guid playerId, byte[] data) => db.Database.ExecuteSqlInterpolatedAsync(
            $"""INSERT INTO "AvatarImages" ("Id", "PlayerId", "Data", "CreatedAt") VALUES ({Guid.NewGuid()}, {playerId}, {data}, now())""");

        async Task RefusedAsync(string sqlState, Func<Task> change) =>
            Assert.Equal(sqlState, (await Assert.ThrowsAsync<PostgresException>(change)).SqlState);

        await RefusedAsync(PostgresErrorCodes.UniqueViolation, () => InsertAsync(profile.PlayerId, [1]));
        await RefusedAsync(PostgresErrorCodes.ForeignKeyViolation, () => InsertAsync(Guid.NewGuid(), [1]));
        await RefusedAsync(PostgresErrorCodes.ForeignKeyViolation, () => db.Database.ExecuteSqlInterpolatedAsync(
            $"""UPDATE "Players" SET "AvatarImageId" = {Guid.NewGuid()} WHERE "Id" = {profile.PlayerId}"""));
        await db.Database.ExecuteSqlRawAsync("DELETE FROM \"AvatarImages\"");
        await RefusedAsync(PostgresErrorCodes.CheckViolation, () => InsertAsync(profile.PlayerId, []));
        await RefusedAsync(PostgresErrorCodes.CheckViolation, () => InsertAsync(profile.PlayerId, new byte[1024 * 1024 + 1]));
        await RefusedAsync(PostgresErrorCodes.NotNullViolation, () => InsertAsync(profile.PlayerId, null!));
        await InsertAsync(profile.PlayerId, new byte[1024 * 1024]);

        await db.Database.ExecuteSqlInterpolatedAsync($"""DELETE FROM "Players" WHERE "Id" = {profile.PlayerId}""");
        Assert.Empty(await StoredAsync());
    }
}
