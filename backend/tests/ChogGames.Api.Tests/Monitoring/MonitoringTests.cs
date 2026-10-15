using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using ChogGames.Api.Monitoring;
using ChogGames.Api.Realtime;
using ChogGames.Core.Rooms;
using ChogGames.Infrastructure.Persistence;
using ChogGames.Tests.Wallets;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ChogGames.Api.Tests.Monitoring;

/// <summary>Game and session metrics, and what never reaches the logs, against PostgreSQL (STORY-048).</summary>
public sealed partial class MonitoringTests(MigratedPostgresDatabase database) : IClassFixture<MigratedPostgresDatabase>, IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly CapturingLoggerProvider _logs = new();
    private readonly List<IAsyncDisposable> _disposables = [];
    private ApiFactory _factory = null!;

    private sealed record PlayerDto(Guid PlayerId, string DisplayName, string Kind, string? WalletAddress);

    private sealed record RoomDto(string Code, string Status);

    private sealed record ChallengeDto(Guid ChallengeId, string Message);

    private sealed record Guest(HttpClient Client, PlayerDto Player, string Cookie);

    public async Task InitializeAsync()
    {
        if (database.ConnectionString.Length > 0)
        {
            await using var db = database.CreateDbContext();
            await db.Database.ExecuteSqlRawAsync("TRUNCATE \"GuestSessions\", \"Players\" CASCADE");
        }

        _factory = new ApiFactory(
            database.ConnectionString,
            new Dictionary<string, string?> { ["Games:Items:0:Status"] = "Available" },
            _logs.AddTo);
    }

    public async Task DisposeAsync()
    {
        foreach (var disposable in _disposables)
        {
            await disposable.DisposeAsync();
        }

        await _factory.DisposeAsync();
    }

    private async Task<Guest> GuestAsync(string name)
    {
        var client = _factory.CreateHttpsClient();
        using var response = await client.PostAsJsonAsync("/api/guest-sessions", new { displayName = name });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var cookie = response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("chog_session=", StringComparison.Ordinal)).Split(';')[0];
        return new Guest(client, (await response.Content.ReadFromJsonAsync<PlayerDto>(Json))!, cookie);
    }

    /// <summary>The room the response returns; null for 204 No Content.</summary>
    private static async Task<RoomDto?> SendAsync(HttpClient client, HttpMethod method, string path, object? body = null)
    {
        using var request = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body) };
        using var response = await client.SendAsync(request);
        Assert.True(response.IsSuccessStatusCode, $"{method} {path}: {response.StatusCode}");
        return response.StatusCode == HttpStatusCode.NoContent ? null : await response.Content.ReadFromJsonAsync<RoomDto>(Json);
    }

    private async Task<string> MetricsAsync()
    {
        using var client = _factory.CreateClient();
        return await client.GetStringAsync(MonitoringEndpoints.MetricsPath);
    }

    private static string[] Lines(string metrics) => metrics.Split('\n', StringSplitOptions.RemoveEmptyEntries);

    private async Task<HubConnection> ConnectAsync(Guest guest)
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
        _disposables.Add(connection);
        await connection.StartAsync();
        return connection;
    }

    private static RoomCode Code(string value) => RoomCode.TryParse(value, out var code) ? code : throw new ArgumentException(value);

    [GeneratedRegex("^Nonce: ([0-9a-f]{32})$", RegexOptions.Multiline)]
    private static partial Regex NonceLine();

    [PostgresFact]
    public async Task Room_lifecycle_sessions_and_subscriptions_are_counted_with_no_player_or_room_identifiers()
    {
        var alice = await GuestAsync("Alice");
        var bob = await GuestAsync("Bob");
        var cancelled = (await SendAsync(alice.Client, HttpMethod.Post, "/api/rooms", new { gameType = "chog-life" }))!;
        await SendAsync(alice.Client, HttpMethod.Post, $"/api/rooms/{cancelled.Code}/leave");
        var room = (await SendAsync(alice.Client, HttpMethod.Post, "/api/rooms", new { gameType = "chog-life" }))!;
        await SendAsync(bob.Client, HttpMethod.Post, $"/api/rooms/{room.Code}/join");

        var waiting = Lines(await MetricsAsync());
        Assert.Contains("choggames_rooms_open{game=\"chog-life\",status=\"waiting\"} 1", waiting);
        Assert.Contains("choggames_rooms_open{game=\"chog-life\",status=\"active\"} 0", waiting);

        await SendAsync(alice.Client, HttpMethod.Put, $"/api/rooms/{room.Code}/ready", new { ready = true });
        await SendAsync(bob.Client, HttpMethod.Put, $"/api/rooms/{room.Code}/ready", new { ready = true });
        Assert.Equal("Active", (await SendAsync(alice.Client, HttpMethod.Post, $"/api/rooms/{room.Code}/start"))!.Status);
        var connection = await ConnectAsync(alice);
        await connection.InvokeAsync<JsonElement>(nameof(RoomHub.SubscribeRoom), room.Code);
        await Assert.ThrowsAsync<HubException>(() => connection.InvokeAsync<JsonElement>(nameof(RoomHub.SubscribeRoom), "ZZZZZZ"));

        var active = Lines(await MetricsAsync());
        Assert.Contains("choggames_rooms_open{game=\"chog-life\",status=\"waiting\"} 0", active);
        Assert.Contains("choggames_rooms_open{game=\"chog-life\",status=\"active\"} 1", active);
        Assert.Contains("signalr_server_active_connections{signalr_transport=\"web_sockets\"} 1", active);

        await SendAsync(bob.Client, HttpMethod.Post, $"/api/rooms/{room.Code}/leave");
        Assert.Equal("Abandoned", (await alice.Client.GetFromJsonAsync<RoomDto>($"/api/rooms/{room.Code}", Json))!.Status);

        var text = await MetricsAsync();
        var ended = Lines(text);
        Assert.Contains("choggames_guest_sessions_created_total 2", ended);
        Assert.Contains("choggames_rooms_created_total{game=\"chog-life\"} 2", ended);
        Assert.Contains("choggames_rooms_cancelled_total{game=\"chog-life\"} 1", ended);
        Assert.Contains("choggames_matches_started_total{game=\"chog-life\"} 1", ended);
        Assert.Contains("choggames_matches_ended_total{game=\"chog-life\",outcome=\"abandoned\"} 1", ended);
        Assert.Contains("choggames_rooms_open{game=\"chog-life\",status=\"active\"} 0", ended);
        Assert.Contains("choggames_realtime_room_subscriptions_total{result=\"subscribed\"} 1", ended);
        Assert.Contains("choggames_realtime_room_subscriptions_total{result=\"room_not_found\"} 1", ended);
        Assert.Contains(ended, line => line.StartsWith("http_server_request_duration_seconds_count{http_request_method=\"POST\",http_response_status_code=\"204\",http_route=\"/api/rooms/{code}/leave\"}", StringComparison.Ordinal));
        foreach (var identifier in new[] { room.Code, cancelled.Code, alice.Player.PlayerId.ToString(), bob.Player.PlayerId.ToString(), "Alice", "Bob" })
        {
            Assert.DoesNotContain(identifier, text);
        }
    }

    [PostgresFact]
    public async Task A_room_change_rolled_back_is_never_counted()
    {
        var alice = await GuestAsync("Alice");
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ChogGamesDbContext>();
            var game = await db.GameDefinitions.SingleAsync(g => g.Id == "chog-life");
            var host = await db.Players.SingleAsync(p => p.Id == alice.Player.PlayerId);

            await using (var transaction = await db.Database.BeginTransactionAsync())
            {
                db.GameRooms.Add(GameRoom.Create(game, host, Code("RBACKA"), DateTimeOffset.UtcNow));
                await db.SaveChangesAsync();
                await transaction.RollbackAsync();
            }

            Assert.DoesNotContain("choggames_rooms_created_total", await MetricsAsync());

            db.ChangeTracker.Clear();
            await using (var transaction = await db.Database.BeginTransactionAsync())
            {
                db.GameRooms.Add(GameRoom.Create(game, host, Code("RBACKB"), DateTimeOffset.UtcNow));
                await db.SaveChangesAsync();
                Assert.DoesNotContain("choggames_rooms_created_total", await MetricsAsync());
                await transaction.CommitAsync();
            }
        }

        Assert.Contains("choggames_rooms_created_total{game=\"chog-life\"} 1", Lines(await MetricsAsync()));
    }

    [PostgresFact]
    public async Task Wallet_sign_ins_are_counted_by_result_and_no_token_signature_or_challenge_is_ever_logged()
    {
        var alice = await GuestAsync("Alice");
        var wallet = TestWallet.Random();
        var secrets = new List<string> { alice.Cookie.Split('=', 2)[1] };

        async Task<HttpStatusCode> VerifyAsync(Func<ChallengeDto, string> sign)
        {
            using var challengeResponse = await alice.Client.PostAsJsonAsync("/api/wallet/challenge", new { address = wallet.ChecksumAddress });
            var challenge = (await challengeResponse.Content.ReadFromJsonAsync<ChallengeDto>(Json))!;
            var signature = sign(challenge);
            secrets.AddRange([signature, NonceLine().Match(challenge.Message).Groups[1].Value, challenge.ChallengeId.ToString()]);
            using var response = await alice.Client.PostAsJsonAsync("/api/wallet/verify", new { challenge.ChallengeId, signature });
            if (response.Headers.TryGetValues("Set-Cookie", out var cookies))
            {
                secrets.AddRange(cookies.Where(c => c.StartsWith("chog_session=", StringComparison.Ordinal)).Select(c => c.Split(';')[0].Split('=', 2)[1]));
            }

            return response.StatusCode;
        }

        Assert.Equal(HttpStatusCode.BadRequest, await VerifyAsync(_ => TestWallet.Random().SignPersonal("something else")));
        Assert.Equal(HttpStatusCode.OK, await VerifyAsync(challenge => wallet.SignPersonal(challenge.Message)));
        using (var me = await alice.Client.GetAsync("/api/me"))
        {
            Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        }

        var metrics = Lines(await MetricsAsync());
        Assert.Contains("choggames_wallet_sign_ins_total{result=\"invalid_signature\"} 1", metrics);
        Assert.Contains("choggames_wallet_sign_ins_total{result=\"succeeded\"} 1", metrics);

        // Every level, every category, including framework and EF Core logs.
        Assert.NotEmpty(_logs.Entries);
        Assert.Equal(2, secrets.Count(s => s.Length == 132));
        foreach (var secret in secrets.Where(s => s.Length > 0))
        {
            Assert.DoesNotContain(_logs.Entries, entry => entry.Text.Contains(secret, StringComparison.OrdinalIgnoreCase));
        }
    }
}
