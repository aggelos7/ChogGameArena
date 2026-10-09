using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using ChogGames.Tests.Wallets;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ChogGames.Api.Tests.Wallet;

public sealed partial class WalletSignInTests(MigratedPostgresDatabase database) : IClassFixture<MigratedPostgresDatabase>, IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // Real time truncated to whole seconds: cookie jars drop cookies expired in real time, and PostgreSQL stores microseconds.
    private readonly ManualTimeProvider _clock = new(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
    private ApiFactory _factory = null!;

    private sealed record PlayerDto(Guid PlayerId, string DisplayName, string Kind, string? WalletAddress);

    private sealed record ChallengeDto(Guid ChallengeId, string Message, DateTimeOffset ExpiresAt);

    private sealed record ProblemDto(Dictionary<string, string[]> Errors);

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

    private ApiFactory CreateFactory(IReadOnlyDictionary<string, string?>? settings = null) =>
        new(database.ConnectionString, settings, services => services.AddSingleton<TimeProvider>(_clock));

    private sealed record Guest(HttpClient Client, PlayerDto Player, string SessionCookie) : IDisposable
    {
        public void Dispose() => Client.Dispose();
    }

    private async Task<Guest> GuestAsync(string displayName, ApiFactory? factory = null)
    {
        var client = (factory ?? _factory).CreateHttpsClient();
        using var response = await client.PostAsJsonAsync("/api/guest-sessions", new { displayName });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return new Guest(client, (await response.Content.ReadFromJsonAsync<PlayerDto>(Json))!, SessionCookie(response));
    }

    private static string SessionCookie(HttpResponseMessage response) =>
        response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("chog_session=", StringComparison.Ordinal)).Split(';')[0];

    private static async Task<ChallengeDto> ChallengeAsync(HttpClient client, string address)
    {
        using var response = await client.PostAsJsonAsync("/api/wallet/challenge", new { address });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ChallengeDto>(Json))!;
    }

    private static Task<HttpResponseMessage> VerifyAsync(HttpClient client, Guid challengeId, string? signature) =>
        client.PostAsJsonAsync("/api/wallet/verify", new { challengeId, signature });

    private static async Task<PlayerDto> SignInAsync(HttpClient client, TestWallet wallet)
    {
        var challenge = await ChallengeAsync(client, wallet.ChecksumAddress);
        using var response = await VerifyAsync(client, challenge.ChallengeId, wallet.SignPersonal(challenge.Message));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<PlayerDto>(Json))!;
    }

    private static async Task<PlayerDto> MeAsync(HttpClient client) => (await client.GetFromJsonAsync<PlayerDto>("/api/me", Json))!;

    private static async Task AssertProblemAsync(HttpResponseMessage response, string field)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = (await response.Content.ReadFromJsonAsync<ProblemDto>(Json))!;
        Assert.Equal([field], problem.Errors.Keys);
    }

    private async Task<HttpStatusCode> MeStatusWithCookieAsync(string cookie)
    {
        using var client = _factory.CreateHttpsClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/me");
        request.Headers.Add("Cookie", cookie);
        using var response = await client.SendAsync(request);
        return response.StatusCode;
    }

    [GeneratedRegex("^Nonce: ([0-9a-f]{32})$", RegexOptions.Multiline)]
    private static partial Regex NonceLine();

    [PostgresFact]
    public async Task Challenge_is_an_eip4361_message_bound_to_domain_chain_address_nonce_and_expiry()
    {
        using var alice = await GuestAsync("Alice");
        var wallet = TestWallet.Random();

        var challenge = await ChallengeAsync(alice.Client, wallet.Address.Value.ToUpperInvariant().Replace("0X", "0x"));
        var lines = challenge.Message.Split('\n');

        Assert.Equal($"{ApiFactory.WalletAuthDomain} wants you to sign in with your Ethereum account:", lines[0]);
        Assert.Equal(wallet.ChecksumAddress, lines[1]);
        Assert.Equal("", lines[2]);
        Assert.StartsWith("Sign in to Chog Game Arena.", lines[3]);
        Assert.Equal(
            [$"URI: {ApiFactory.WalletAuthUri}", "Version: 1", "Chain ID: 143"],
            lines[5..8]);
        Assert.Matches(NonceLine(), challenge.Message);
        Assert.Equal($"Issued At: {_clock.Now.UtcDateTime:yyyy-MM-dd'T'HH:mm:ss.fff'Z'}", lines[9]);
        Assert.Equal($"Expiration Time: {_clock.Now.AddMinutes(5).UtcDateTime:yyyy-MM-dd'T'HH:mm:ss.fff'Z'}", lines[10]);
        Assert.Equal(_clock.Now.AddMinutes(5), challenge.ExpiresAt);
    }

    [PostgresFact]
    public async Task Every_challenge_has_a_fresh_nonce()
    {
        using var alice = await GuestAsync("Alice");
        var wallet = TestWallet.Random();

        var first = await ChallengeAsync(alice.Client, wallet.ChecksumAddress);
        var second = await ChallengeAsync(alice.Client, wallet.ChecksumAddress);

        Assert.NotEqual(NonceLine().Match(first.Message).Groups[1].Value, NonceLine().Match(second.Message).Groups[1].Value);
    }

    [PostgresFact]
    public async Task Valid_signature_upgrades_the_guest_keeping_its_identity_and_rotates_the_session()
    {
        using var alice = await GuestAsync("Alice");
        var wallet = TestWallet.Random();

        var signedIn = await SignInAsync(alice.Client, wallet);

        Assert.Equal(new PlayerDto(alice.Player.PlayerId, "Alice", "Wallet", wallet.Address.Value), signedIn);
        Assert.Equal(signedIn, await MeAsync(alice.Client));
        Assert.Equal(HttpStatusCode.Unauthorized, await MeStatusWithCookieAsync(alice.SessionCookie));
    }

    [PostgresFact]
    public async Task An_address_alone_never_authenticates()
    {
        using var alice = await GuestAsync("Alice");
        var wallet = TestWallet.Random();
        var challenge = await ChallengeAsync(alice.Client, wallet.ChecksumAddress);

        using var withoutSignature = await VerifyAsync(alice.Client, challenge.ChallengeId, null);
        await AssertProblemAsync(withoutSignature, "signature");

        var other = await ChallengeAsync(alice.Client, wallet.ChecksumAddress);
        using var withAddressAsSignature = await VerifyAsync(alice.Client, other.ChallengeId, wallet.ChecksumAddress);
        await AssertProblemAsync(withAddressAsSignature, "signature");

        Assert.Equal(alice.Player, await MeAsync(alice.Client));
    }

    [PostgresFact]
    public async Task A_signature_from_another_wallet_fails()
    {
        using var alice = await GuestAsync("Alice");
        var claimed = TestWallet.Random();
        var attacker = TestWallet.Random();
        var challenge = await ChallengeAsync(alice.Client, claimed.ChecksumAddress);

        using var response = await VerifyAsync(alice.Client, challenge.ChallengeId, attacker.SignPersonal(challenge.Message));

        await AssertProblemAsync(response, "signature");
        Assert.Equal(alice.Player, await MeAsync(alice.Client));
    }

    [PostgresFact]
    public async Task A_replayed_signature_fails()
    {
        using var alice = await GuestAsync("Alice");
        var wallet = TestWallet.Random();
        var challenge = await ChallengeAsync(alice.Client, wallet.ChecksumAddress);
        var signature = wallet.SignPersonal(challenge.Message);
        using var first = await VerifyAsync(alice.Client, challenge.ChallengeId, signature);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        using var replay = await VerifyAsync(alice.Client, challenge.ChallengeId, signature);

        await AssertProblemAsync(replay, "challengeId");
    }

    [PostgresFact]
    public async Task A_failed_attempt_uses_up_the_challenge()
    {
        using var alice = await GuestAsync("Alice");
        var wallet = TestWallet.Random();
        var challenge = await ChallengeAsync(alice.Client, wallet.ChecksumAddress);
        using var wrong = await VerifyAsync(alice.Client, challenge.ChallengeId, TestWallet.Random().SignPersonal(challenge.Message));
        await AssertProblemAsync(wrong, "signature");

        using var retry = await VerifyAsync(alice.Client, challenge.ChallengeId, wallet.SignPersonal(challenge.Message));

        await AssertProblemAsync(retry, "challengeId");
        Assert.Equal("Guest", (await MeAsync(alice.Client)).Kind);
    }

    [PostgresFact]
    public async Task An_expired_challenge_fails()
    {
        using var alice = await GuestAsync("Alice");
        var wallet = TestWallet.Random();
        var challenge = await ChallengeAsync(alice.Client, wallet.ChecksumAddress);
        var signature = wallet.SignPersonal(challenge.Message);

        _clock.Advance(TimeSpan.FromMinutes(5));
        using var response = await VerifyAsync(alice.Client, challenge.ChallengeId, signature);

        await AssertProblemAsync(response, "challengeId");
        Assert.Equal("Guest", (await MeAsync(alice.Client)).Kind);
    }

    [PostgresTheory]
    [InlineData(ApiFactory.WalletAuthDomain + " wants", "evil.example wants")]
    [InlineData("URI: " + ApiFactory.WalletAuthUri, "URI: https://evil.example")]
    [InlineData("Chain ID: 143", "Chain ID: 1")]
    [InlineData("Expiration Time: ", "Expiration Time: 2099")]
    public async Task A_signature_over_an_altered_message_fails(string original, string replacement)
    {
        using var alice = await GuestAsync("Alice");
        var wallet = TestWallet.Random();
        var challenge = await ChallengeAsync(alice.Client, wallet.ChecksumAddress);
        Assert.Contains(original, challenge.Message);

        var altered = challenge.Message.Replace(original, replacement, StringComparison.Ordinal);
        using var response = await VerifyAsync(alice.Client, challenge.ChallengeId, wallet.SignPersonal(altered));

        await AssertProblemAsync(response, "signature");
        Assert.Equal("Guest", (await MeAsync(alice.Client)).Kind);
    }

    [PostgresFact]
    public async Task A_challenge_cannot_be_used_from_another_session()
    {
        using var alice = await GuestAsync("Alice");
        using var bob = await GuestAsync("Bob");
        var wallet = TestWallet.Random();
        var aliceChallenge = await ChallengeAsync(alice.Client, wallet.ChecksumAddress);
        var signature = wallet.SignPersonal(aliceChallenge.Message);

        using var stolen = await VerifyAsync(bob.Client, aliceChallenge.ChallengeId, signature);

        await AssertProblemAsync(stolen, "challengeId");
        Assert.Equal(bob.Player, await MeAsync(bob.Client));
        using var own = await VerifyAsync(alice.Client, aliceChallenge.ChallengeId, signature);
        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
    }

    [PostgresFact]
    public async Task A_wallet_already_bound_to_a_player_signs_in_as_that_player_and_leaves_the_guest_alone()
    {
        using var alice = await GuestAsync("Alice");
        var wallet = TestWallet.Random();
        await SignInAsync(alice.Client, wallet);
        using var otherBrowser = await GuestAsync("Visitor");

        var signedIn = await SignInAsync(otherBrowser.Client, wallet);

        Assert.Equal(alice.Player.PlayerId, signedIn.PlayerId);
        Assert.Equal("Alice", signedIn.DisplayName);
        await using var db = database.CreateDbContext();
        var visitor = await db.Players.SingleAsync(p => p.Id == otherBrowser.Player.PlayerId);
        Assert.Equal(("Guest", null), (visitor.Kind.ToString(), visitor.WalletAddress));
    }

    [PostgresFact]
    public async Task Signing_in_with_a_second_wallet_creates_a_separate_wallet_player()
    {
        using var alice = await GuestAsync("Alice");
        var first = await SignInAsync(alice.Client, TestWallet.Random());
        var secondWallet = TestWallet.Random();

        var second = await SignInAsync(alice.Client, secondWallet);

        Assert.NotEqual(first.PlayerId, second.PlayerId);
        Assert.Equal(("Wallet", secondWallet.Address.Value), (second.Kind, second.WalletAddress));
    }

    [PostgresFact]
    public async Task Parallel_verifications_of_one_challenge_succeed_at_most_once()
    {
        using var alice = await GuestAsync("Alice");
        var wallet = TestWallet.Random();
        var challenge = await ChallengeAsync(alice.Client, wallet.ChecksumAddress);
        var signature = wallet.SignPersonal(challenge.Message);

        var responses = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => VerifyAsync(alice.Client, challenge.ChallengeId, signature)));

        // Exactly one wins. The rest fail either on the used-up challenge (400) or, if they are authenticated after
        // the winner rotated the shared session, on the revoked cookie (401); which one depends on timing.
        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.All(
            responses.Where(r => r.StatusCode != HttpStatusCode.OK),
            r => Assert.Contains(r.StatusCode, new[] { HttpStatusCode.BadRequest, HttpStatusCode.Unauthorized }));
        await using var db = database.CreateDbContext();
        Assert.Equal(1, await db.GuestSessions.CountAsync(s => s.PlayerId == alice.Player.PlayerId && s.RevokedAt == null));
        foreach (var response in responses)
        {
            response.Dispose();
        }
    }

    [PostgresFact]
    public async Task Sign_out_ends_the_session_and_the_next_visit_gets_a_new_guest()
    {
        using var alice = await GuestAsync("Alice");
        var wallet = TestWallet.Random();
        var signedIn = await SignInAsync(alice.Client, wallet);

        using var signOut = await alice.Client.PostAsync("/api/wallet/sign-out", null);

        Assert.Equal(HttpStatusCode.NoContent, signOut.StatusCode);
        Assert.Contains(signOut.Headers.GetValues("Set-Cookie"), c => c.StartsWith("chog_session=;", StringComparison.Ordinal));
        using var me = await alice.Client.GetAsync("/api/me");
        Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);
        using var guest = await alice.Client.PostAsJsonAsync("/api/guest-sessions", new { });
        var newGuest = (await guest.Content.ReadFromJsonAsync<PlayerDto>(Json))!;
        Assert.Equal(("Guest", null), (newGuest.Kind, newGuest.WalletAddress));
        Assert.NotEqual(signedIn.PlayerId, newGuest.PlayerId);
    }

    [PostgresTheory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0x1234")]
    [InlineData("not-an-address")]
    [InlineData("0xg000000000000000000000000000000000000000")]
    public async Task Invalid_addresses_are_rejected_without_creating_a_challenge(string? address)
    {
        using var alice = await GuestAsync("Alice");

        using var response = await alice.Client.PostAsJsonAsync("/api/wallet/challenge", new { address });

        await AssertProblemAsync(response, "address");
        await using var db = database.CreateDbContext();
        Assert.Equal(0, await db.WalletChallenges.CountAsync());
    }

    [PostgresFact]
    public async Task Verify_requires_a_challenge_id()
    {
        using var alice = await GuestAsync("Alice");

        using var response = await alice.Client.PostAsJsonAsync("/api/wallet/verify", new { signature = "0x00" });

        await AssertProblemAsync(response, "challengeId");
    }

    [PostgresTheory]
    [InlineData("/api/wallet/challenge")]
    [InlineData("/api/wallet/verify")]
    [InlineData("/api/wallet/sign-out")]
    public async Task Endpoints_require_a_session(string path)
    {
        using var client = _factory.CreateHttpsClient();

        using var response = await client.PostAsJsonAsync(path, new { });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [PostgresFact]
    public async Task Challenge_and_verify_are_rate_limited_per_client()
    {
        await using var factory = CreateFactory(new Dictionary<string, string?> { ["WalletAuth:RequestsPerMinutePerClient"] = "2" });
        using var alice = await GuestAsync("Alice", factory);
        var address = TestWallet.Random().ChecksumAddress;

        using var first = await alice.Client.PostAsJsonAsync("/api/wallet/challenge", new { address });
        using var second = await alice.Client.PostAsJsonAsync("/api/wallet/verify", new { challengeId = Guid.NewGuid(), signature = "0x" });
        using var third = await alice.Client.PostAsJsonAsync("/api/wallet/challenge", new { address });

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, second.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);
    }

    [PostgresFact]
    public async Task Housekeeping_removes_the_players_spent_challenges()
    {
        using var alice = await GuestAsync("Alice");
        var wallet = TestWallet.Random();
        var spent = await ChallengeAsync(alice.Client, wallet.ChecksumAddress);
        using var _ = await VerifyAsync(alice.Client, spent.ChallengeId, null);

        await ChallengeAsync(alice.Client, wallet.ChecksumAddress);

        await using var db = database.CreateDbContext();
        Assert.False(await db.WalletChallenges.AnyAsync(c => c.Id == spent.ChallengeId));
        Assert.Equal(1, await db.WalletChallenges.CountAsync());
    }

    [PostgresFact]
    public async Task Database_rejects_a_wallet_bound_to_two_players_or_a_guest_with_a_wallet()
    {
        using var alice = await GuestAsync("Alice");
        using var bob = await GuestAsync("Bob");
        var wallet = TestWallet.Random();
        await SignInAsync(alice.Client, wallet);
        await using var db = database.CreateDbContext();

        var duplicate = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"Players\" SET \"Kind\" = 'Wallet', \"WalletAddress\" = {wallet.Address.Value} WHERE \"Id\" = {bob.Player.PlayerId}"));
        Assert.Equal("23505", duplicate.SqlState);

        var guestWithWallet = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"Players\" SET \"WalletAddress\" = {TestWallet.Random().Address.Value} WHERE \"Id\" = {bob.Player.PlayerId}"));
        Assert.Equal("CK_Players_Wallet_MatchesKind", guestWithWallet.ConstraintName);
    }
}
