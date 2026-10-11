using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ChogGames.Core.Nfts;
using ChogGames.Core.Wallets;
using ChogGames.Tests.Wallets;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ChogGames.Api.Tests.Rooms;

public sealed class LobbyEndpointTests(MigratedPostgresDatabase database) : IClassFixture<MigratedPostgresDatabase>, IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly FakeNftProvider _chain = new();
    private ApiFactory _factory = null!;

    private sealed record CharacterDto(string Kind, string Id, string Name, string? Color);

    private sealed record MemberDto(
        string DisplayName, string AvatarId, bool IsHost, bool IsYou, bool IsReady, CharacterDto Character, string? Ability);

    private sealed record RoomDto(string Code, string GameType, string Status, int MinPlayers, int Capacity, MemberDto[] Members);

    private sealed record ProblemDto(int Status, string? Code, Dictionary<string, string[]>? Errors);

    private sealed record ChallengeDto(Guid ChallengeId, string Message);

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
            services => services.AddScoped<INftProvider>(_ => _chain));
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    private async Task<HttpClient> GuestAsync(string name)
    {
        var client = _factory.CreateHttpsClient();
        using var response = await client.PostAsJsonAsync("/api/guest-sessions", new { displayName = name });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return client;
    }

    /// <summary>A browser signed in with a fresh wallet that holds token 7 and has selected it.</summary>
    private async Task<(HttpClient Client, WalletAddress Wallet)> HolderAsync(string name)
    {
        var client = await GuestAsync(name);
        var wallet = TestWallet.Random();
        using var challengeResponse = await client.PostAsJsonAsync("/api/wallet/challenge", new { address = wallet.ChecksumAddress });
        var challenge = (await challengeResponse.Content.ReadFromJsonAsync<ChallengeDto>(Json))!;
        using var verify = await client.PostAsJsonAsync(
            "/api/wallet/verify", new { challenge.ChallengeId, signature = wallet.SignPersonal(challenge.Message) });
        Assert.Equal(HttpStatusCode.OK, verify.StatusCode);
        _chain.Holdings[wallet.Address] = [new OwnedNft(NftTokenId.From(7), "Chog #7", null, [])];
        using var select = await client.PutAsJsonAsync("/api/characters/selected", new { kind = "Nft", id = "7" });
        Assert.Equal(HttpStatusCode.OK, select.StatusCode);
        return (client, wallet.Address);
    }

    private static async Task<RoomDto> RoomAsync(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        Assert.Equal(expected, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<RoomDto>(Json))!;
    }

    private static async Task<RoomDto> CreatedRoomAsync(HttpClient host)
    {
        using var response = await host.PostAsJsonAsync("/api/rooms", new { gameType = "chog-life" });
        return await RoomAsync(response, HttpStatusCode.Created);
    }

    private static async Task<RoomDto> JoinedAsync(HttpClient client, string code)
    {
        using var response = await client.PostAsync($"/api/rooms/{code}/join", null);
        return await RoomAsync(response);
    }

    private static Task<HttpResponseMessage> ReadyAsync(HttpClient client, string code, object? body) =>
        client.PutAsJsonAsync($"/api/rooms/{code}/ready", body);

    private static async Task<RoomDto> ReadiedAsync(HttpClient client, string code, bool ready = true)
    {
        using var response = await ReadyAsync(client, code, new { ready });
        return await RoomAsync(response);
    }

    private static Task<HttpResponseMessage> StartAsync(HttpClient client, string code) => client.PostAsync($"/api/rooms/{code}/start", null);

    private static Task<HttpResponseMessage> LeaveAsync(HttpClient client, string code) => client.PostAsync($"/api/rooms/{code}/leave", null);

    private static async Task ProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = (await response.Content.ReadFromJsonAsync<ProblemDto>(Json))!;
        Assert.Equal(((int)status, code), (problem.Status, problem.Code));
    }

    private static async Task<RoomDto> ViewAsync(HttpClient client, string code) =>
        (await client.GetFromJsonAsync<RoomDto>($"/api/rooms/{code}", Json))!;

    private async Task<(string Code, HttpClient Alice, HttpClient Bob)> PairAsync()
    {
        var alice = await GuestAsync("Alice");
        var bob = await GuestAsync("Bob");
        var room = await CreatedRoomAsync(alice);
        await JoinedAsync(bob, room.Code);
        return (room.Code, alice, bob);
    }

    [PostgresFact]
    public async Task Lobby_shows_each_players_chog_ready_status_and_an_empty_ability()
    {
        var (code, alice, bob) = await PairAsync();
        using (alice)
        using (bob)
        {
            using var mint = await bob.PutAsJsonAsync("/api/characters/selected", new { kind = "Standard", id = "mint" });
            Assert.Equal(HttpStatusCode.OK, mint.StatusCode);

            var room = await ReadiedAsync(bob, code);
            var seenByHost = await ViewAsync(alice, code);

            Assert.Equal(
                [
                    new MemberDto("Alice", "classic", IsHost: true, IsYou: false, IsReady: false, new("Standard", "classic", "Classic", "#5b2bd6"), null),
                    new MemberDto("Bob", "classic", IsHost: false, IsYou: true, IsReady: true, new("Standard", "mint", "Mint", "#1a7f5a"), null),
                ],
                room.Members);
            Assert.Equal(room.Members.Select(m => (m.DisplayName, m.IsReady, m.Character)), seenByHost.Members.Select(m => (m.DisplayName, m.IsReady, m.Character)));
        }
    }

    [PostgresFact]
    public async Task Ready_can_be_withdrawn()
    {
        var (code, alice, bob) = await PairAsync();
        using (alice)
        using (bob)
        {
            await ReadiedAsync(bob, code);

            var room = await ReadiedAsync(bob, code, ready: false);

            Assert.All(room.Members, m => Assert.False(m.IsReady));
        }
    }

    [PostgresFact]
    public async Task Choosing_another_chog_shows_the_player_as_not_ready()
    {
        var (code, alice, bob) = await PairAsync();
        using (alice)
        using (bob)
        {
            await ReadiedAsync(bob, code);

            (await bob.PutAsJsonAsync("/api/characters/selected", new { kind = "Standard", id = "gold" })).Dispose();

            var bobSeen = (await ViewAsync(alice, code)).Members.Single(m => m.DisplayName == "Bob");
            Assert.Equal((false, "gold"), (bobSeen.IsReady, bobSeen.Character.Id));
        }
    }

    [PostgresTheory]
    [InlineData("{}")]
    [InlineData("{\"ready\":null}")]
    [InlineData("{\"ready\":\"yes\"}")]
    public async Task Ready_needs_true_or_false(string body)
    {
        var (code, alice, bob) = await PairAsync();
        using (alice)
        using (bob)
        {
            using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
            using var response = await bob.PutAsync($"/api/rooms/{code}/ready", content);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.All((await ViewAsync(alice, code)).Members, m => Assert.False(m.IsReady));
        }
    }

    [PostgresFact]
    public async Task Host_starts_once_everyone_is_ready_and_both_see_it_started()
    {
        var (code, alice, bob) = await PairAsync();
        using (alice)
        using (bob)
        {
            await ReadiedAsync(alice, code);
            await ReadiedAsync(bob, code);

            using var response = await StartAsync(alice, code);

            Assert.Equal("Active", (await RoomAsync(response)).Status);
            Assert.Equal("Active", (await ViewAsync(bob, code)).Status);
            await ProblemAsync(await StartAsync(alice, code), HttpStatusCode.Conflict, "room_started");
            await ProblemAsync(await ReadyAsync(bob, code, new { ready = false }), HttpStatusCode.Conflict, "room_started");
        }
    }

    [PostgresFact]
    public async Task Start_is_refused_until_the_lobby_is_valid()
    {
        using var alice = await GuestAsync("Alice");
        using var bob = await GuestAsync("Bob");
        var code = (await CreatedRoomAsync(alice)).Code;
        await ReadiedAsync(alice, code);

        await ProblemAsync(await StartAsync(alice, code), HttpStatusCode.Conflict, "not_enough_players");

        await JoinedAsync(bob, code);
        await ProblemAsync(await StartAsync(alice, code), HttpStatusCode.Conflict, "players_not_ready");

        await ReadiedAsync(bob, code);
        await ProblemAsync(await StartAsync(bob, code), HttpStatusCode.Forbidden, "not_host");

        Assert.Equal("Waiting", (await ViewAsync(alice, code)).Status);
    }

    [PostgresFact]
    public async Task Non_members_cannot_ready_or_start()
    {
        var (code, alice, bob) = await PairAsync();
        using (alice)
        using (bob)
        using (var mallory = await GuestAsync("Mallory"))
        {
            await ProblemAsync(await ReadyAsync(mallory, code, new { ready = true }), HttpStatusCode.NotFound, "room_not_found");
            await ProblemAsync(await StartAsync(mallory, code), HttpStatusCode.NotFound, "room_not_found");
            await ProblemAsync(await StartAsync(mallory, "not-a-code"), HttpStatusCode.NotFound, "room_not_found");
            await ProblemAsync(await ReadyAsync(mallory, "K7QF0M", new { ready = true }), HttpStatusCode.NotFound, "room_not_found");
        }
    }

    [PostgresFact]
    public async Task Nft_chog_shows_its_token_and_is_checked_live_at_start()
    {
        using var alice = await GuestAsync("Alice");
        var (holder, wallet) = await HolderAsync("Holder");
        using (holder)
        {
            var code = (await CreatedRoomAsync(alice)).Code;
            var joined = await JoinedAsync(holder, code);
            Assert.Equal(new CharacterDto("Nft", "7", "Chog #7", null), joined.Members.Single(m => m.IsYou).Character);
            await ReadiedAsync(alice, code);
            await ReadiedAsync(holder, code);
            _chain.LiveChecks.Clear();

            using var response = await StartAsync(alice, code);

            Assert.Equal("Active", (await RoomAsync(response)).Status);
            Assert.Equal((wallet, "7"), Assert.Single(_chain.LiveChecks));
        }
    }

    [PostgresFact]
    public async Task Transferred_nft_blocks_the_start_and_unreadies_its_player()
    {
        using var alice = await GuestAsync("Alice");
        var (holder, wallet) = await HolderAsync("Holder");
        using (holder)
        {
            var code = (await CreatedRoomAsync(alice)).Code;
            await JoinedAsync(holder, code);
            await ReadiedAsync(alice, code);
            await ReadiedAsync(holder, code);
            _chain.Holdings[wallet] = [];

            await ProblemAsync(await StartAsync(alice, code), HttpStatusCode.Conflict, "character_unavailable");

            var room = await ViewAsync(alice, code);
            Assert.Equal("Waiting", room.Status);
            Assert.Equal([true, false], room.Members.Select(m => m.IsReady));
        }
    }

    [PostgresFact]
    public async Task Start_waits_while_ownership_cannot_be_read()
    {
        using var alice = await GuestAsync("Alice");
        var (holder, _) = await HolderAsync("Holder");
        using (holder)
        {
            var code = (await CreatedRoomAsync(alice)).Code;
            await JoinedAsync(holder, code);
            await ReadiedAsync(alice, code);
            await ReadiedAsync(holder, code);
            _chain.Down = true;

            await ProblemAsync(await StartAsync(alice, code), HttpStatusCode.ServiceUnavailable, "ownership_unavailable");

            var room = await ViewAsync(alice, code);
            Assert.Equal(("Waiting", true), (room.Status, room.Members.All(m => m.IsReady)));
        }
    }

    [PostgresFact]
    public async Task A_member_leaves_and_is_free_to_create_a_room()
    {
        var (code, alice, bob) = await PairAsync();
        using (alice)
        using (bob)
        {
            using var response = await LeaveAsync(bob, code);

            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
            var room = await ViewAsync(alice, code);
            Assert.Equal("Waiting", room.Status);
            Assert.Equal(["Alice"], room.Members.Select(m => m.DisplayName));
            await ProblemAsync(await bob.GetAsync($"/api/rooms/{code}"), HttpStatusCode.NotFound, "room_not_found");
            await CreatedRoomAsync(bob);
        }
    }

    [PostgresFact]
    public async Task The_host_leaving_closes_the_room_for_everyone()
    {
        var (code, alice, bob) = await PairAsync();
        using (alice)
        using (bob)
        using (var carol = await GuestAsync("Carol"))
        {
            using var response = await LeaveAsync(alice, code);

            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
            Assert.Equal("Cancelled", (await ViewAsync(bob, code)).Status);
            await ProblemAsync(await carol.PostAsync($"/api/rooms/{code}/join", null), HttpStatusCode.Conflict, "room_closed");
            await ProblemAsync(await LeaveAsync(bob, code), HttpStatusCode.Conflict, "room_closed");
            await CreatedRoomAsync(bob);
            await CreatedRoomAsync(alice);
        }
    }

    [PostgresFact]
    public async Task Leaving_is_refused_for_non_members_and_started_rooms()
    {
        var (code, alice, bob) = await PairAsync();
        using (alice)
        using (bob)
        using (var mallory = await GuestAsync("Mallory"))
        {
            await ProblemAsync(await LeaveAsync(mallory, code), HttpStatusCode.NotFound, "room_not_found");
            await ProblemAsync(await LeaveAsync(mallory, "not-a-code"), HttpStatusCode.NotFound, "room_not_found");

            await ReadiedAsync(alice, code);
            await ReadiedAsync(bob, code);
            (await StartAsync(alice, code)).Dispose();

            await ProblemAsync(await LeaveAsync(bob, code), HttpStatusCode.Conflict, "room_started");
            Assert.Equal(2, (await ViewAsync(alice, code)).Members.Length);
        }
    }

    /// <summary>Chain stand-in: holdings per wallet, an outage switch and a record of live ownership checks.</summary>
    private sealed class FakeNftProvider : INftProvider
    {
        public ConcurrentDictionary<WalletAddress, List<OwnedNft>> Holdings { get; } = new();

        public ConcurrentQueue<(WalletAddress Wallet, string TokenId)> LiveChecks { get; } = new();

        public bool Down { get; set; }

        public Task<IReadOnlyList<OwnedNft>> GetOwnedAsync(WalletAddress owner, CancellationToken cancellationToken)
        {
            ThrowIfDown();
            return Task.FromResult<IReadOnlyList<OwnedNft>>(Holdings.GetValueOrDefault(owner) ?? []);
        }

        public Task<OwnedNft?> FindOwnedAsync(WalletAddress owner, NftTokenId tokenId, CancellationToken cancellationToken)
        {
            ThrowIfDown();
            LiveChecks.Enqueue((owner, tokenId.ToString()));
            return Task.FromResult((Holdings.GetValueOrDefault(owner) ?? []).FirstOrDefault(n => n.TokenId == tokenId));
        }

        private void ThrowIfDown()
        {
            if (Down)
            {
                throw new NftProviderUnavailableException("Chain is down.");
            }
        }
    }
}
