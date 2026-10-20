using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ChogGames.Core.Players;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using SkiaSharp;

namespace ChogGames.Api.Tests.Profile;

/// <summary>Terms of use, photo reports, hiding and the operators' review (STORY-069, DEC-070).</summary>
public sealed class PhotoReportTests(MigratedPostgresDatabase database) : IClassFixture<MigratedPostgresDatabase>, IAsyncLifetime
{
    private const string Upload = "/api/profile/avatar-image";
    private const string Operator = "0x00000000000000000000000000000000000000aa";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly List<ApiFactory> _factories = [];
    private ApiFactory _factory = null!;

    private sealed record AvatarDto(string Id, string? ImageUrl);

    private sealed record ProfileDto(Guid PlayerId, string DisplayName, AvatarDto Avatar, bool TermsAccepted, bool AvatarUnderReview, bool Operator);

    private sealed record ProblemDto(int Status, string? Code);

    private sealed record ReportedPhotoDto(
        Guid ImageId, string ImageUrl, string OwnerName, int Reporters, Dictionary<string, int> Reasons, bool Hidden);

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

    private ApiFactory Factory(int reportsPerWindow = 100)
    {
        var factory = new ApiFactory(database.ConnectionString, new Dictionary<string, string?>
        {
            ["AvatarImages:ChangesPerWindowPerPlayer"] = "100",
            ["GuestSessions:CreatePerMinutePerClient"] = "1000",
            ["Games:Items:0:Status"] = "Available",
            ["Moderation:OperatorWallets"] = $"0x00000000000000000000000000000000000000bb, {Operator.ToUpperInvariant().Replace("0X", "0x")}",
            ["Moderation:ReportsPerWindowPerPlayer"] = reportsPerWindow.ToString(),
            ["Legal:ContactEmail"] = "privacy@arena.example.test",
        });
        _factories.Add(factory);
        return factory;
    }

    private async Task<HttpClient> GuestAsync(string displayName, bool acceptTerms = true, ApiFactory? factory = null)
    {
        var client = (factory ?? _factory).CreateHttpsClient();
        using var response = await client.PostAsJsonAsync("/api/guest-sessions", new { displayName });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        if (acceptTerms)
        {
            using var accepted = await AcceptAsync(client, TermsOfUse.CurrentVersion);
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        }

        return client;
    }

    /// <summary>A guest made an operator by binding them to the operator wallet directly, as a sign-in would.</summary>
    private async Task<HttpClient> OperatorAsync()
    {
        var client = await GuestAsync("Olga");
        var playerId = (await ProfileAsync(client)).PlayerId;
        await using var db = database.CreateDbContext();
        await db.Database.ExecuteSqlAsync($"""UPDATE "Players" SET "Kind" = 'Wallet', "WalletAddress" = {Operator} WHERE "Id" = {playerId}""");
        return client;
    }

    private static Task<HttpResponseMessage> AcceptAsync(HttpClient client, string? version) =>
        client.PostAsJsonAsync("/api/profile/terms", new { version });

    private static async Task<HttpResponseMessage> PutPhotoAsync(HttpClient client)
    {
        using var surface = SKSurface.Create(new SKImageInfo(300, 300));
        surface.Canvas.Clear(SKColors.Teal);
        using var data = surface.Snapshot().Encode(SKEncodedImageFormat.Png, 90);
        var content = new ByteArrayContent(data.ToArray());
        content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        return await client.PutAsync(Upload, content);
    }

    private static async Task<string> PhotoAsync(HttpClient client)
    {
        using var response = await PutPhotoAsync(client);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ProfileDto>(Json))!.Avatar.ImageUrl!;
    }

    private static Task<HttpResponseMessage> ReportAsync(HttpClient client, string photoUrl, string? reason = "sexual") =>
        client.PostAsJsonAsync($"{photoUrl}/reports", new { reason });

    private static async Task<ProfileDto> ProfileAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<ProfileDto>("/api/profile", Json))!;

    private static async Task AssertRefusedAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(code, (await response.Content.ReadFromJsonAsync<ProblemDto>(Json))!.Code);
    }

    private async Task<(string Reason, bool Dismissed)[]> StoredReportsAsync()
    {
        await using var db = database.CreateDbContext();
        return [.. (await db.AvatarImageReports.AsNoTracking().OrderBy(r => r.CreatedAt).ToListAsync())
            .Select(r => (r.Reason.ToString(), r.DismissedAt is not null))];
    }

    private async Task<bool> HiddenAsync(string photoUrl)
    {
        var imageId = Guid.Parse(photoUrl.Split('/')[^1]);
        await using var db = database.CreateDbContext();
        return await db.AvatarImages.AsNoTracking().Where(i => i.Id == imageId).Select(i => i.HiddenAt != null).SingleAsync();
    }

    private async Task<string> ReportedByThreeAsync(HttpClient owner)
    {
        var photo = await PhotoAsync(owner);
        foreach (var name in new[] { "Rita", "Sam", "Tess" })
        {
            using var reporter = await GuestAsync(name);
            using var reported = await ReportAsync(reporter, photo, "hate");
            Assert.Equal(HttpStatusCode.NoContent, reported.StatusCode);
        }

        return photo;
    }

    [PostgresFact]
    public async Task Uploading_needs_the_current_terms_accepted_once()
    {
        using var alice = await GuestAsync("Alice", acceptTerms: false);
        Assert.False((await ProfileAsync(alice)).TermsAccepted);

        await AssertRefusedAsync(await PutPhotoAsync(alice), HttpStatusCode.Forbidden, "terms_not_accepted");
        await AssertRefusedAsync(await AcceptAsync(alice, "2020-01-01"), HttpStatusCode.Conflict, "terms_outdated");
        await AssertRefusedAsync(await AcceptAsync(alice, null), HttpStatusCode.Conflict, "terms_outdated");
        await using (var db = database.CreateDbContext())
        {
            Assert.Empty(await db.AvatarImages.ToListAsync());
            Assert.Null((await db.Players.SingleAsync()).TermsVersion);
        }

        using (var accepted = await AcceptAsync(alice, TermsOfUse.CurrentVersion))
        {
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
            Assert.True((await accepted.Content.ReadFromJsonAsync<ProfileDto>(Json))!.TermsAccepted);
        }

        DateTimeOffset? firstAccepted;
        await using (var db = database.CreateDbContext())
        {
            firstAccepted = (await db.Players.SingleAsync()).TermsAcceptedAt;
        }

        (await AcceptAsync(alice, TermsOfUse.CurrentVersion)).Dispose();
        await using (var db = database.CreateDbContext())
        {
            var player = await db.Players.SingleAsync();
            Assert.Equal(TermsOfUse.CurrentVersion, player.TermsVersion);
            Assert.Equal(firstAccepted, player.TermsAcceptedAt);
        }

        Assert.NotNull(await PhotoAsync(alice));
    }

    [PostgresFact]
    public async Task Terms_from_an_older_version_must_be_accepted_again_before_uploading()
    {
        using var alice = await GuestAsync("Alice");
        await using (var db = database.CreateDbContext())
        {
            await db.Database.ExecuteSqlRawAsync("UPDATE \"Players\" SET \"TermsVersion\" = '2026-01-01'");
        }

        Assert.False((await ProfileAsync(alice)).TermsAccepted);
        await AssertRefusedAsync(await PutPhotoAsync(alice), HttpStatusCode.Forbidden, "terms_not_accepted");
    }

    [PostgresFact]
    public async Task The_legal_details_are_public()
    {
        using var anonymous = _factory.CreateHttpsClient();

        var legal = await anonymous.GetFromJsonAsync<JsonElement>("/api/legal", Json);

        Assert.Equal(TermsOfUse.CurrentVersion, legal.GetProperty("termsVersion").GetString());
        Assert.Equal("privacy@arena.example.test", legal.GetProperty("contactEmail").GetString());
    }

    [PostgresFact]
    public async Task A_player_reports_another_players_photo_once_with_a_fixed_reason()
    {
        using var alice = await GuestAsync("Alice");
        using var bob = await GuestAsync("Bob", acceptTerms: false);
        var photo = await PhotoAsync(alice);

        using (var reported = await ReportAsync(bob, photo, "Violence"))
        {
            Assert.Equal(HttpStatusCode.NoContent, reported.StatusCode);
        }

        await AssertRefusedAsync(await ReportAsync(bob, photo, "scam"), HttpStatusCode.Conflict, "photo_already_reported");
        await AssertRefusedAsync(await ReportAsync(alice, photo), HttpStatusCode.BadRequest, "report_own_photo");
        foreach (var reason in new[] { null, "", "1", "rude", "sexual content" })
        {
            using var carol = await GuestAsync($"Carol{reason?.Length}");
            await AssertRefusedAsync(await ReportAsync(carol, photo, reason), HttpStatusCode.BadRequest, "invalid_report_reason");
        }

        Assert.Equal(HttpStatusCode.NotFound, (await ReportAsync(bob, $"/api/avatar-images/{Guid.NewGuid()}")).StatusCode);
        using var anonymous = _factory.CreateHttpsClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await ReportAsync(anonymous, photo)).StatusCode);

        Assert.Equal([("Violence", false)], await StoredReportsAsync());
        Assert.False(await HiddenAsync(photo));
    }

    [PostgresFact]
    public async Task The_third_reporter_hides_the_photo_from_everyone_but_its_owner_and_operators()
    {
        using var alice = await GuestAsync("Alice");
        using var bob = await GuestAsync("Bob");
        using var olga = await OperatorAsync();
        var photo = await PhotoAsync(alice);
        using var created = await alice.PostAsJsonAsync("/api/rooms", new { gameType = "chog-life" });
        var code = (await created.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("code").GetString();
        (await bob.PostAsync($"/api/rooms/{code}/join", null)).Dispose();

        async Task<string?> AlicesPhotoFor(HttpClient viewer)
        {
            var alice = (await viewer.GetFromJsonAsync<JsonElement>($"/api/rooms/{code}", Json)).GetProperty("members").EnumerateArray()
                .Single(m => m.GetProperty("displayName").GetString() == "Alice");
            // The pawn's photo and the profile avatar's (STORY-085) always agree.
            Assert.Equal(alice.GetProperty("avatarImageUrl").GetString(), alice.GetProperty("avatar").GetProperty("imageUrl").GetString());
            return alice.GetProperty("avatarImageUrl").GetString();
        }

        foreach (var name in new[] { "Rita", "Sam" })
        {
            using var reporter = await GuestAsync(name);
            (await ReportAsync(reporter, photo)).Dispose();
        }

        Assert.False(await HiddenAsync(photo));
        Assert.Equal(photo, await AlicesPhotoFor(bob));

        using (var tess = await GuestAsync("Tess"))
        {
            (await ReportAsync(tess, photo, "other")).Dispose();
        }

        Assert.True(await HiddenAsync(photo));
        Assert.Null(await AlicesPhotoFor(bob));
        Assert.Equal(HttpStatusCode.NotFound, (await bob.GetAsync(photo)).StatusCode);
        Assert.Equal(photo, await AlicesPhotoFor(alice));
        Assert.Equal(HttpStatusCode.OK, (await alice.GetAsync(photo)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await olga.GetAsync(photo)).StatusCode);
        var profile = await ProfileAsync(alice);
        Assert.True(profile.AvatarUnderReview);
        Assert.Equal(photo, profile.Avatar.ImageUrl);
        Assert.False((await ProfileAsync(bob)).AvatarUnderReview);
    }

    [PostgresFact]
    public async Task Reports_sent_at_once_count_each_reporter_once()
    {
        using var alice = await GuestAsync("Alice");
        var photo = await PhotoAsync(alice);
        var reporters = new List<HttpClient>();
        for (var i = 0; i < 6; i++)
        {
            reporters.Add(await GuestAsync($"Reporter{i}"));
        }

        var responses = await Task.WhenAll(reporters.Concat(reporters).Select(r => ReportAsync(r, photo)));

        Assert.Equal(6, responses.Count(r => r.StatusCode == HttpStatusCode.NoContent));
        Assert.Equal(6, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        Assert.Equal(6, (await StoredReportsAsync()).Length);
        Assert.True(await HiddenAsync(photo));
        foreach (var disposable in responses.Concat<IDisposable>(reporters))
        {
            disposable.Dispose();
        }
    }

    [PostgresFact]
    public async Task Reports_are_rate_limited_per_player()
    {
        var factory = Factory(reportsPerWindow: 2);
        using var alice = await GuestAsync("Alice", factory: factory);
        using var bob = await GuestAsync("Bob", factory: factory);
        var photo = await PhotoAsync(alice);

        (await ReportAsync(bob, photo, "nonsense")).Dispose();
        (await ReportAsync(bob, photo)).Dispose();

        Assert.Equal(HttpStatusCode.TooManyRequests, (await ReportAsync(bob, photo, "scam")).StatusCode);
        Assert.Equal([("Sexual", false)], await StoredReportsAsync());
    }

    [PostgresFact]
    public async Task Only_operators_see_the_reports_and_act_on_them()
    {
        using var alice = await GuestAsync("Alice");
        using var bob = await GuestAsync("Bob");
        var photo = await ReportedByThreeAsync(alice);
        var imageId = photo.Split('/')[^1];

        // A wallet player who isn't listed, and a guest, find nothing here.
        await using (var db = database.CreateDbContext())
        {
            var bobsId = (await ProfileAsync(bob)).PlayerId;
            await db.Database.ExecuteSqlAsync(
                $"""UPDATE "Players" SET "Kind" = 'Wallet', "WalletAddress" = '0x00000000000000000000000000000000000000cc' WHERE "Id" = {bobsId}""");
        }

        foreach (var outsider in new[] { bob, alice })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await outsider.GetAsync("/api/moderation/reports")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await outsider.DeleteAsync($"/api/moderation/photos/{imageId}")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await outsider.PostAsync($"/api/moderation/photos/{imageId}/dismiss", null)).StatusCode);
            Assert.False((await ProfileAsync(outsider)).Operator);
        }

        Assert.True(await HiddenAsync(photo));
        Assert.Equal(3, (await StoredReportsAsync()).Length);

        using var olga = await OperatorAsync();
        Assert.True((await ProfileAsync(olga)).Operator);
        var listed = Assert.Single((await olga.GetFromJsonAsync<ReportedPhotoDto[]>("/api/moderation/reports", Json))!);
        Assert.Equal(photo, listed.ImageUrl);
        Assert.Equal("Alice", listed.OwnerName);
        Assert.Equal(3, listed.Reporters);
        Assert.Equal(new Dictionary<string, int> { ["hate"] = 3 }, listed.Reasons);
        Assert.True(listed.Hidden);
    }

    [PostgresFact]
    public async Task Dismissing_shows_the_photo_again_and_its_reporters_cannot_report_it_again()
    {
        using var alice = await GuestAsync("Alice");
        using var bob = await GuestAsync("Bob");
        using var olga = await OperatorAsync();
        var photo = await ReportedByThreeAsync(alice);

        using (var dismissed = await olga.PostAsync($"/api/moderation/photos/{photo.Split('/')[^1]}/dismiss", null))
        {
            Assert.Equal(HttpStatusCode.NoContent, dismissed.StatusCode);
        }

        Assert.False(await HiddenAsync(photo));
        Assert.False((await ProfileAsync(alice)).AvatarUnderReview);
        Assert.Equal(HttpStatusCode.OK, (await bob.GetAsync(photo)).StatusCode);
        Assert.Empty((await olga.GetFromJsonAsync<ReportedPhotoDto[]>("/api/moderation/reports", Json))!);
        Assert.All(await StoredReportsAsync(), r => Assert.True(r.Dismissed));

        // A new reporter counts again from one.
        (await ReportAsync(bob, photo, "scam")).Dispose();
        Assert.False(await HiddenAsync(photo));
        Assert.Equal(1, Assert.Single((await olga.GetFromJsonAsync<ReportedPhotoDto[]>("/api/moderation/reports", Json))!).Reporters);
    }

    [PostgresFact]
    public async Task Removing_deletes_the_photo_and_its_reports()
    {
        using var alice = await GuestAsync("Alice");
        using var olga = await OperatorAsync();
        var photo = await ReportedByThreeAsync(alice);
        var path = $"/api/moderation/photos/{photo.Split('/')[^1]}";

        using (var removed = await olga.DeleteAsync(path))
        {
            Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        }

        Assert.Equal(HttpStatusCode.NotFound, (await olga.DeleteAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await olga.PostAsync($"{path}/dismiss", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await alice.GetAsync(photo)).StatusCode);
        var profile = await ProfileAsync(alice);
        Assert.Null(profile.Avatar.ImageUrl);
        Assert.False(profile.AvatarUnderReview);
        Assert.Empty(await StoredReportsAsync());
        Assert.Empty((await olga.GetFromJsonAsync<ReportedPhotoDto[]>("/api/moderation/reports", Json))!);
    }

    [PostgresTheory]
    [InlineData("""UPDATE "Players" SET "TermsVersion" = '2026-10-19'""", "CK_Players_TermsAccepted")]
    [InlineData("""UPDATE "Players" SET "TermsAcceptedAt" = now()""", "CK_Players_TermsAccepted")]
    [InlineData("""UPDATE "AvatarImages" SET "HiddenAt" = "CreatedAt" - interval '1 second'""", "CK_AvatarImages_HiddenAfterCreated")]
    [InlineData("""UPDATE "AvatarImageReports" SET "Reason" = 'Rude'""", "CK_AvatarImageReports_Reason")]
    [InlineData("""UPDATE "AvatarImageReports" SET "DismissedAt" = "CreatedAt" - interval '1 second'""", "CK_AvatarImageReports_DismissedAfterCreated")]
    public async Task The_database_refuses_inconsistent_terms_hiding_and_reports(string sql, string constraint)
    {
        using var alice = await GuestAsync("Alice");
        using var bob = await GuestAsync("Bob", acceptTerms: false);
        (await ReportAsync(bob, await PhotoAsync(alice))).Dispose();
        await using (var db = database.CreateDbContext())
        {
            // Bob has never accepted, so his row tests the terms pair from NULL.
            await db.Database.ExecuteSqlRawAsync("UPDATE \"Players\" SET \"TermsVersion\" = NULL, \"TermsAcceptedAt\" = NULL");
        }

        await using var context = database.CreateDbContext();
        var error = await Assert.ThrowsAsync<PostgresException>(() => context.Database.ExecuteSqlRawAsync(sql));

        Assert.Equal(constraint, error.ConstraintName);
    }

    [Theory]
    [InlineData("Moderation:OperatorWallets", "0x123")]
    [InlineData("Moderation:OperatorWallets", "0x00000000000000000000000000000000000000aa, admin")]
    [InlineData("Moderation:HideAfterReporters", "0")]
    [InlineData("Moderation:ReportsPerWindowPerPlayer", "0")]
    [InlineData("Moderation:Window", "00:00:00")]
    [InlineData("Legal:ContactEmail", "privacy")]
    [InlineData("Legal:ContactEmail", "a@b@c")]
    [InlineData("Legal:ContactEmail", "<a@b.c>")]
    public void Invalid_moderation_or_legal_settings_stop_startup(string key, string value)
    {
        var error = StartupValidation.Failure(new Dictionary<string, string?> { [key] = value });

        Assert.Contains(key.Split(':')[0], error.Message);
    }
}
