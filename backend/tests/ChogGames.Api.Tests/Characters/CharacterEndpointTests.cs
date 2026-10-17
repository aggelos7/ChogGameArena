using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ChogGames.Core.Nfts;
using ChogGames.Core.Wallets;
using ChogGames.Tests.Wallets;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace ChogGames.Api.Tests.Characters;

public sealed class CharacterEndpointTests(MigratedPostgresDatabase database) : IClassFixture<MigratedPostgresDatabase>, IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static readonly OwnedNft Chog1 = Nft(1, "CHOG #1462", [new("Background", "Purple"), new("Eyes", "Laser")]);
    private static readonly OwnedNft Chog7 = Nft(7, "CHOG #7", []);
    private static readonly OwnedNft Chog1969 = new(NftTokenId.From(1969), "Chog #1969", null, [new("Body", "Golden")]);

    private readonly FakeNftProvider _chain = new();
    private ApiFactory _factory = null!;

    private sealed record TraitDto(string Type, string Value);

    private sealed record NftDto(string TokenId, string Name, string? ImageUrl, TraitDto[] Traits);

    private sealed record StandardDto(string Id, string Name, string Color);

    private sealed record SelectedDto(string Kind, string Id);

    private sealed record CharactersDto(string NftStatus, NftDto[] Nfts, StandardDto[] Standard, SelectedDto Selected);

    private sealed record ChallengeDto(Guid ChallengeId, string Message);

    private sealed record ProblemDto(Dictionary<string, string[]>? Errors, string? Detail);

    private sealed record BadgeDto(string Id, string Name);

    private sealed record MilestoneDto(string Id, string Name, int MinChogs);

    private sealed record CollectorDto(int ChogsHeld, BadgeDto? Badge, MilestoneDto? Next);

    private sealed record TiersDto(MilestoneDto[] Tiers);

    private sealed record ProfileDto(CollectorDto Collector);

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
        new(database.ConnectionString, settings, services => services.AddScoped<INftProvider>(_ => _chain));

    private static OwnedNft Nft(ulong id, string name, NftTrait[] traits) =>
        new(NftTokenId.From(id), name, new Uri($"https://gateway.example.test/ipfs/bafy/{id}.webp"), traits);

    private async Task<HttpClient> GuestAsync(ApiFactory? factory = null)
    {
        var client = (factory ?? _factory).CreateHttpsClient();
        using var response = await client.PostAsJsonAsync("/api/guest-sessions", new { displayName = "Alice" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return client;
    }

    /// <summary>A browser signed in with a fresh wallet holding <paramref name="tokens"/>.</summary>
    private async Task<(HttpClient Client, WalletAddress Wallet)> HolderAsync(OwnedNft[] tokens, ApiFactory? factory = null)
    {
        var client = await GuestAsync(factory);
        var wallet = TestWallet.Random();
        using var challengeResponse = await client.PostAsJsonAsync("/api/wallet/challenge", new { address = wallet.ChecksumAddress });
        var challenge = (await challengeResponse.Content.ReadFromJsonAsync<ChallengeDto>(Json))!;
        using var verify = await client.PostAsJsonAsync(
            "/api/wallet/verify", new { challenge.ChallengeId, signature = wallet.SignPersonal(challenge.Message) });
        Assert.Equal(HttpStatusCode.OK, verify.StatusCode);
        _chain.Holdings[wallet.Address] = [.. tokens];
        return (client, wallet.Address);
    }

    private static async Task<CharactersDto> GetAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/api/characters");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<CharactersDto>(Json))!;
    }

    private static async Task<CollectorDto> CollectorAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<ProfileDto>("/api/profile", Json))!.Collector;

    private static OwnedNft[] Chogs(int count) =>
        [.. Enumerable.Range(1, count).Select(i => Nft((ulong)i, $"CHOG #{i}", []))];

    private static Task<HttpResponseMessage> SelectAsync(HttpClient client, object? body) =>
        client.PutAsJsonAsync("/api/characters/selected", body);

    private static async Task AssertProblemAsync(HttpResponseMessage response, string field)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = (await response.Content.ReadFromJsonAsync<ProblemDto>(Json))!;
        Assert.Equal([field], problem.Errors!.Keys);
    }

    [PostgresFact]
    public async Task Guest_sees_standard_chogs_and_is_asked_to_sign_in_for_nfts()
    {
        using var client = await GuestAsync();

        var characters = await GetAsync(client);

        Assert.Equal("NoWallet", characters.NftStatus);
        Assert.Empty(characters.Nfts);
        Assert.Equal(["classic", "mint", "sunset", "ocean", "berry", "gold"], characters.Standard.Select(s => s.Id));
        Assert.Equal(new SelectedDto("Standard", "classic"), characters.Selected);
        Assert.Equal(0, _chain.Calls);
    }

    [PostgresFact]
    public async Task Holder_sees_every_verified_nft_with_image_token_id_and_traits()
    {
        var (client, _) = await HolderAsync([Chog1, Chog7, Chog1969]);

        var characters = await GetAsync(client);

        Assert.Equal("Available", characters.NftStatus);
        Assert.Equal(
            [
                new NftDto("1", "CHOG #1462", "https://gateway.example.test/ipfs/bafy/1.webp", [new("Background", "Purple"), new("Eyes", "Laser")]),
                new NftDto("7", "CHOG #7", "https://gateway.example.test/ipfs/bafy/7.webp", []),
                new NftDto("1969", "Chog #1969", null, [new("Body", "Golden")]),
            ],
            characters.Nfts,
            new NftDtoComparer());
        Assert.Equal(6, characters.Standard.Length);
        Assert.Equal(new SelectedDto("Standard", "classic"), characters.Selected);
    }

    [PostgresFact]
    public async Task Wallet_without_nfts_can_still_select_a_standard_chog()
    {
        var (client, _) = await HolderAsync([]);

        var before = await GetAsync(client);
        using var response = await SelectAsync(client, new { kind = "Standard", id = "ocean" });

        Assert.Equal(("Available", 0), (before.NftStatus, before.Nfts.Length));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new SelectedDto("Standard", "ocean"), (await GetAsync(client)).Selected);
    }

    [PostgresFact]
    public async Task Guest_can_select_a_standard_chog_and_it_persists()
    {
        using var client = await GuestAsync();

        using var response = await SelectAsync(client, new { kind = "Standard", id = "gold" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new SelectedDto("Standard", "gold"), await response.Content.ReadFromJsonAsync<SelectedDto>(Json));
        Assert.Equal(new SelectedDto("Standard", "gold"), (await GetAsync(client)).Selected);
    }

    [PostgresFact]
    public async Task Holder_selects_an_owned_nft_after_a_live_ownership_check()
    {
        var (client, wallet) = await HolderAsync([Chog1, Chog7]);

        using var response = await SelectAsync(client, new { kind = "Nft", id = "7" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal([(wallet, "7")], _chain.LiveChecks);
        Assert.Equal(new SelectedDto("Nft", "7"), (await GetAsync(client)).Selected);
    }

    [PostgresFact]
    public async Task Nft_no_longer_owned_is_refused_even_if_it_was_listed_before()
    {
        var (client, wallet) = await HolderAsync([Chog1, Chog7]);
        Assert.Equal(2, (await GetAsync(client)).Nfts.Length);
        _chain.Holdings[wallet] = [Chog1];

        using var response = await SelectAsync(client, new { kind = "Nft", id = "7" });

        await AssertProblemAsync(response, "id");
        Assert.Equal(new SelectedDto("Standard", "classic"), (await GetAsync(client)).Selected);
    }

    [PostgresFact]
    public async Task Another_wallets_nft_is_refused()
    {
        var (client, _) = await HolderAsync([Chog1]);
        var (_, other) = await HolderAsync([Chog7]);
        Assert.NotEqual(default, other);

        using var response = await SelectAsync(client, new { kind = "Nft", id = "7" });

        await AssertProblemAsync(response, "id");
        Assert.Equal(new SelectedDto("Standard", "classic"), (await GetAsync(client)).Selected);
    }

    [PostgresFact]
    public async Task Guest_cannot_select_an_nft_and_no_ownership_check_is_made()
    {
        using var client = await GuestAsync();

        using var response = await SelectAsync(client, new { kind = "Nft", id = "1" });

        await AssertProblemAsync(response, "kind");
        Assert.Empty(_chain.LiveChecks);
        Assert.Equal(new SelectedDto("Standard", "classic"), (await GetAsync(client)).Selected);
    }

    public static TheoryData<string?, string?, string> InvalidSelections => new()
    {
        { null, "classic", "kind" },
        { "standard", "classic", "kind" },
        { "0", "classic", "kind" },
        { "Avatar", "classic", "kind" },
        { "Standard", null, "id" },
        { "Standard", "unicorn", "id" },
        { "Standard", "Classic", "id" },
        { "Nft", null, "id" },
        { "Nft", "", "id" },
        { "Nft", "07", "id" },
        { "Nft", "-7", "id" },
        { "Nft", "7.0", "id" },
        { "Nft", "0x7", "id" },
        { "Nft", new string('9', 79), "id" },
    };

    [PostgresTheory]
    [MemberData(nameof(InvalidSelections))]
    public async Task Invalid_selections_are_refused_and_change_nothing(string? kind, string? id, string field)
    {
        var (client, _) = await HolderAsync([Chog7]);
        using (var first = await SelectAsync(client, new { kind = "Nft", id = "7" }))
        {
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        }

        using var response = await SelectAsync(client, new { kind, id });

        await AssertProblemAsync(response, field);
        Assert.Equal(new SelectedDto("Nft", "7"), (await GetAsync(client)).Selected);
    }

    [PostgresFact]
    public async Task Empty_body_is_refused()
    {
        using var client = await GuestAsync();
        using var content = new StringContent("null", System.Text.Encoding.UTF8, "application/json");

        using var response = await client.PutAsync("/api/characters/selected", content);

        await AssertProblemAsync(response, "kind");
    }

    [PostgresFact]
    public async Task During_an_outage_standard_chogs_are_still_offered_and_the_stored_choice_is_kept()
    {
        var (client, _) = await HolderAsync([Chog7]);
        using (var first = await SelectAsync(client, new { kind = "Nft", id = "7" }))
        {
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        }

        _chain.Down = true;
        var characters = await GetAsync(client);

        Assert.Equal("Unavailable", characters.NftStatus);
        Assert.Empty(characters.Nfts);
        Assert.Equal(6, characters.Standard.Length);
        Assert.Equal(new SelectedDto("Nft", "7"), characters.Selected);
    }

    [PostgresFact]
    public async Task During_an_outage_nft_selection_fails_with_503_and_changes_nothing()
    {
        var (client, _) = await HolderAsync([Chog1, Chog7]);
        _chain.Down = true;

        using var response = await SelectAsync(client, new { kind = "Nft", id = "7" });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        _chain.Down = false;
        Assert.Equal(new SelectedDto("Standard", "classic"), (await GetAsync(client)).Selected);
    }

    [PostgresFact]
    public async Task A_selected_nft_that_left_the_wallet_shows_the_classic_chog_as_selected()
    {
        var (client, wallet) = await HolderAsync([Chog1, Chog7]);
        using (var first = await SelectAsync(client, new { kind = "Nft", id = "7" }))
        {
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        }

        _chain.Holdings[wallet] = [Chog1];

        Assert.Equal(new SelectedDto("Standard", "classic"), (await GetAsync(client)).Selected);
    }

    [PostgresFact]
    public async Task Requests_without_a_session_are_refused()
    {
        using var client = _factory.CreateHttpsClient();

        using var get = await client.GetAsync("/api/characters");
        using var put = await SelectAsync(client, new { kind = "Standard", id = "mint" });

        Assert.Equal((HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized), (get.StatusCode, put.StatusCode));
        Assert.Equal(0, _chain.Calls);
    }

    [PostgresFact]
    public async Task Requests_are_rate_limited_per_player()
    {
        await using var factory = CreateFactory(new Dictionary<string, string?> { ["Nfts:RequestsPerMinutePerPlayer"] = "2" });
        var (alice, _) = await HolderAsync([Chog1], factory);
        var (bob, _) = await HolderAsync([Chog7], factory);

        await GetAsync(alice);
        using (var second = await SelectAsync(alice, new { kind = "Nft", id = "1" }))
        {
            Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        }

        var callsBefore = _chain.Calls;
        using var third = await alice.GetAsync("/api/characters");

        Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);
        Assert.Equal(callsBefore, _chain.Calls);
        Assert.Equal("Available", (await GetAsync(bob)).NftStatus);
    }

    [PostgresFact]
    public async Task The_database_refuses_an_nft_selection_for_a_guest()
    {
        using var client = await GuestAsync();
        await GetAsync(client);

        await using var db = database.CreateDbContext();
        var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(
            "UPDATE \"Players\" SET \"SelectedCharacterKind\" = 'Nft', \"SelectedCharacterId\" = '1'"));

        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
    }

    [PostgresFact]
    public async Task A_gallery_read_records_the_chogs_held_and_the_profile_shows_the_collector_tier()
    {
        var (client, _) = await HolderAsync(Chogs(3));
        var before = await CollectorAsync(client);

        await GetAsync(client);

        Assert.Equal(new CollectorDto(0, null, new MilestoneDto("holder", "Holder", 1)), before);
        Assert.Equal(new CollectorDto(3, new BadgeDto("collector", "Collector"), new MilestoneDto("patron", "Patron", 5)), await CollectorAsync(client));
    }

    [PostgresFact]
    public async Task The_profile_lists_every_collector_tier_from_the_lowest_for_guests_and_holders()
    {
        using var guest = await GuestAsync();
        var (holder, _) = await HolderAsync(Chogs(12));
        await GetAsync(holder);

        MilestoneDto[] expected =
            [new("holder", "Holder", 1), new("collector", "Collector", 3), new("patron", "Patron", 5), new("whale", "Whale", 10)];
        foreach (var client in new[] { guest, holder })
        {
            var profile = await client.GetFromJsonAsync<JsonElement>("/api/profile", Json);
            Assert.Equal(expected, profile.GetProperty("collector").Deserialize<TiersDto>(Json)!.Tiers);
        }
    }

    [PostgresTheory]
    [InlineData(1, "holder", "collector")]
    [InlineData(4, "collector", "patron")]
    [InlineData(5, "patron", "whale")]
    [InlineData(12, "whale", null)]
    public async Task The_highest_milestone_reached_is_the_tier(int held, string tier, string? next)
    {
        var (client, _) = await HolderAsync(Chogs(held));

        await GetAsync(client);
        var collector = await CollectorAsync(client);

        Assert.Equal((held, tier, next), (collector.ChogsHeld, collector.Badge?.Id, collector.Next?.Id));
    }

    [PostgresFact]
    public async Task The_tier_follows_current_holdings_down_as_well_as_up()
    {
        var (client, wallet) = await HolderAsync(Chogs(5));
        await GetAsync(client);

        _chain.Holdings[wallet] = [Chog1];
        await GetAsync(client);
        var sold = await CollectorAsync(client);
        _chain.Holdings[wallet] = [];
        await GetAsync(client);

        Assert.Equal((1, "holder"), (sold.ChogsHeld, sold.Badge?.Id));
        Assert.Equal(new CollectorDto(0, null, new MilestoneDto("holder", "Holder", 1)), await CollectorAsync(client));
    }

    [PostgresFact]
    public async Task A_failed_ownership_read_keeps_the_recorded_holdings()
    {
        var (client, wallet) = await HolderAsync(Chogs(3));
        await GetAsync(client);

        _chain.Holdings[wallet] = [];
        _chain.Down = true;
        await GetAsync(client);
        var collector = await CollectorAsync(client);

        Assert.Equal((3, "collector"), (collector.ChogsHeld, collector.Badge?.Id));
    }

    [PostgresFact]
    public async Task A_guest_holds_no_chogs_and_profile_reads_never_touch_the_chain()
    {
        using var client = await GuestAsync();

        await GetAsync(client);

        Assert.Equal(new CollectorDto(0, null, new MilestoneDto("holder", "Holder", 1)), await CollectorAsync(client));
        Assert.Equal(0, _chain.Calls);
    }

    [PostgresTheory]
    [InlineData("UPDATE \"Players\" SET \"ChogsHeld\" = 1 WHERE \"Kind\" = 'Guest'")]
    [InlineData("UPDATE \"Players\" SET \"ChogsHeld\" = -1")]
    public async Task The_database_refuses_holdings_for_a_guest_or_below_zero(string sql)
    {
        using var guest = await GuestAsync();
        var (holder, _) = await HolderAsync(Chogs(1));
        await GetAsync(guest);
        await GetAsync(holder);

        await using var db = database.CreateDbContext();
        var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(sql));

        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
    }

    private sealed class NftDtoComparer : IEqualityComparer<NftDto>
    {
        public bool Equals(NftDto? x, NftDto? y) =>
            x is not null && y is not null && (x.TokenId, x.Name, x.ImageUrl) == (y.TokenId, y.Name, y.ImageUrl) && x.Traits.SequenceEqual(y.Traits);

        public int GetHashCode(NftDto obj) => obj.TokenId.GetHashCode();
    }

    /// <summary>Chain stand-in: holdings per wallet, an outage switch and a record of live ownership checks.</summary>
    private sealed class FakeNftProvider : INftProvider
    {
        private int _calls;

        public ConcurrentDictionary<WalletAddress, List<OwnedNft>> Holdings { get; } = new();

        public ConcurrentQueue<(WalletAddress Wallet, string TokenId)> LiveChecks { get; } = new();

        public bool Down { get; set; }

        public int Calls => _calls;

        public Task<IReadOnlyList<OwnedNft>> GetOwnedAsync(WalletAddress owner, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            ThrowIfDown();
            return Task.FromResult<IReadOnlyList<OwnedNft>>(Holdings.GetValueOrDefault(owner) ?? []);
        }

        public Task<OwnedNft?> FindOwnedAsync(WalletAddress owner, NftTokenId tokenId, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
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
