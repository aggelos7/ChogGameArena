using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ChogGames.Api.ChogLife;
using ChogGames.Api.ChogRace;
using ChogGames.Api.ChogRoyale;
using ChogGames.Core.Abilities;
using ChogGames.Core.Nfts;
using ChogGames.Core.Wallets;
using ChogGames.Games.ChogLife.Abilities;
using ChogGames.Tests.Wallets;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ChogGames.Api.Tests.Rooms;

/// <summary>
/// Lobby ability selection and lock (STORY-033, NFT-013, NFT-015, DEC-035): in a game with abilities each player chooses
/// one from their pool before marking ready; others only see that they chose until the start, which locks it with the
/// catalog version. Chog Life gets a test catalog here, with no holder rules; Chog Race's is taken out, as by the test
/// host. With trait-based abilities on (STORY-061, DEC-066) a holder's cached traits narrow their lobby pool and the live
/// ones decide at start. With the shipped holder rules and Chog Life's real catalog (STORY-090, DEC-077) a guest gets the
/// 2 standard abilities with Base values, a holder all 4 with their Tier's, which the live Tier at start locks; Chog Race
/// and, since STORY-093 (DEC-095), Chog Royale split their six abilities the same way.
/// </summary>
public sealed class LobbyAbilityTests(MigratedPostgresDatabase database) : IClassFixture<MigratedPostgresDatabase>, IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly FakeNftProvider _chain = new();
    private ApiFactory _factory = null!;

    private sealed record AbilityDto(string Id, string Name, string Description, string Target, int MaxUses);

    private sealed record BadgeDto(string Id, string Name);

    private sealed record MemberDto(string DisplayName, bool IsYou, bool IsReady, AbilityDto? Ability, bool HasAbility, BadgeDto? Collector = null);

    private sealed record RoomDto(string Code, long Version, string Status, MemberDto[] Members, AbilityDto[] Abilities)
    {
        public MemberDto Member(string name) => Members.Single(m => m.DisplayName == name);
    }

    private sealed record ProblemDto(int Status, string? Code);

    private sealed record ChallengeDto(Guid ChallengeId, string Message);

    public async Task InitializeAsync()
    {
        if (database.ConnectionString.Length > 0)
        {
            await using var db = database.CreateDbContext();
            await db.Database.ExecuteSqlRawAsync("TRUNCATE \"GuestSessions\", \"Players\", \"ChogNfts\" CASCADE");
        }

        _factory = Factory(Catalog(1, Boost(), Reroll()));
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    private static AbilityDefinition Boost(decimal percent = 25) => AbilityDefinition.Create(
        "reward_boost", "Reward Boost", "Raise one reward.", AbilityTargetKind.None, ["before_reward"],
        new Dictionary<string, decimal> { ["percent"] = percent });

    private static AbilityDefinition Reroll() =>
        AbilityDefinition.Create("event_reroll", "Lucky Reroll", "Reroll one event.", AbilityTargetKind.None, ["after_event"]);

    private static AbilityCatalog Catalog(int version, params AbilityDefinition[] abilities) => AbilityCatalog.Create(
        "chog-life", version, abilities, abilities.Select(a => new NoEffect(a.Id, a.Parameters.Keys)), new NoWindow());

    private ApiFactory Factory(AbilityCatalog catalog, IReadOnlyDictionary<string, string?>? settings = null) =>
        new(
            database.ConnectionString,
            new Dictionary<string, string?>(settings ?? new Dictionary<string, string?>())
            {
                ["Games:Items:0:Status"] = "Available",
                ["Games:Items:1:Status"] = "Available",
            },
            services =>
            {
                services.AddScoped<INftProvider>(_ => _chain);
                services.AddSingleton(catalog);
            });

    /// <summary>A browser of the current server that sends its session cookie itself, so it survives a redeploy.</summary>
    private HttpClient Browser(string? cookie = null)
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), HandleCookies = false });
        if (cookie is not null)
        {
            client.DefaultRequestHeaders.Add("Cookie", cookie);
        }

        return client;
    }

    private async Task<HttpClient> GuestAsync(string name)
    {
        var client = Browser();
        using var response = await client.PostAsJsonAsync("/api/guest-sessions", new { displayName = name });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        client.DefaultRequestHeaders.Add("Cookie", response.Headers.GetValues("Set-Cookie").Single().Split(';')[0]);
        return client;
    }

    /// <summary>
    /// A guest who signs in with a fresh wallet holding token 7, with traits, and <paramref name="others"/> more Chogs,
    /// and selects token 7.
    /// </summary>
    private async Task<HttpClient> HolderAsync(string name, int others = 0) => (await HolderWithWalletAsync(name, others)).Client;

    private async Task<(HttpClient Client, WalletAddress Wallet)> HolderWithWalletAsync(string name, int others = 0, NftTrait[]? traits = null)
    {
        var client = await GuestAsync(name);
        var wallet = TestWallet.Random();
        using var challengeResponse = await client.PostAsJsonAsync("/api/wallet/challenge", new { address = wallet.ChecksumAddress });
        var challenge = (await challengeResponse.Content.ReadFromJsonAsync<ChallengeDto>(Json))!;
        using var verify = await client.PostAsJsonAsync(
            "/api/wallet/verify", new { challenge.ChallengeId, signature = wallet.SignPersonal(challenge.Message) });
        Assert.Equal(HttpStatusCode.OK, verify.StatusCode);

        // Signing in may issue a new session cookie; the latest one is what a browser sends.
        if (verify.Headers.TryGetValues("Set-Cookie", out var cookies))
        {
            client.DefaultRequestHeaders.Remove("Cookie");
            client.DefaultRequestHeaders.Add("Cookie", cookies.Single().Split(';')[0]);
        }

        _chain.Holdings[wallet.Address] =
        [
            new OwnedNft(NftTokenId.From(7), "Chog #7", null, traits ?? [new NftTrait("Rarity", "Legendary")]),
            .. Enumerable.Range(100, others).Select(id => new OwnedNft(NftTokenId.From((ulong)id), $"Chog #{id}", null, [])),
        ];
        using var select = await client.PutAsJsonAsync("/api/characters/selected", new { kind = "Nft", id = "7" });
        Assert.Equal(HttpStatusCode.OK, select.StatusCode);
        return (client, wallet.Address);
    }

    private static async Task<RoomDto> RoomAsync(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        Assert.Equal(expected, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<RoomDto>(Json))!;
    }

    private static async Task<RoomDto> ViewAsync(HttpClient client, string code) =>
        (await client.GetFromJsonAsync<RoomDto>($"/api/rooms/{code}", Json))!;

    private static Task<HttpResponseMessage> ChooseAsync(HttpClient client, string code, string? abilityId) =>
        client.PutAsJsonAsync($"/api/rooms/{code}/ability", new { abilityId });

    private static async Task<RoomDto> ChosenAsync(HttpClient client, string code, string abilityId)
    {
        using var response = await ChooseAsync(client, code, abilityId);
        return await RoomAsync(response);
    }

    private static Task<HttpResponseMessage> ReadyAsync(HttpClient client, string code, bool ready = true) =>
        client.PutAsJsonAsync($"/api/rooms/{code}/ready", new { ready });

    private static async Task<RoomDto> ReadiedAsync(HttpClient client, string code)
    {
        using var response = await ReadyAsync(client, code);
        return await RoomAsync(response);
    }

    private static Task<HttpResponseMessage> StartAsync(HttpClient client, string code) => client.PostAsync($"/api/rooms/{code}/start", null);

    private static async Task ProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        var problem = (await response.Content.ReadFromJsonAsync<ProblemDto>(Json))!;
        Assert.Equal(((int)status, code), (problem.Status, problem.Code));
    }

    private async Task<(string Code, HttpClient Alice, HttpClient Bob)> PairAsync(string gameType = "chog-life", HttpClient? bob = null)
    {
        var alice = await GuestAsync("Alice");
        bob ??= await GuestAsync("Bob");
        using var created = await alice.PostAsJsonAsync("/api/rooms", new { gameType });
        var room = await RoomAsync(created, HttpStatusCode.Created);
        using var joined = await bob.PostAsync($"/api/rooms/{room.Code}/join", null);
        Assert.Equal(HttpStatusCode.OK, joined.StatusCode);
        return (room.Code, alice, bob);
    }

    private async Task<RoomMemberRow> StoredMemberAsync(string code, string name)
    {
        await using var db = database.CreateDbContext();
        return await db.Database.SqlQuery<RoomMemberRow>($"""
            SELECT m."AbilityId", m."Ability"::text AS "Ability", m."ReadyCharacterId"
            FROM "RoomMembers" m
            JOIN "GameRooms" r ON r."Id" = m."RoomId"
            JOIN "Players" p ON p."Id" = m."PlayerId"
            WHERE r."Code" = {code} AND p."DisplayName" = {name}
            """).SingleAsync();
    }

    private async Task<StoredAbilityDto> StoredAbilityAsync(string code, string name) =>
        JsonSerializer.Deserialize<StoredAbilityDto>((await StoredMemberAsync(code, name)).Ability!, Json)!;

    /// <summary>The stored shape of a locked ability (DEC-035).</summary>
    private sealed record StoredAbilityDto(
        string Id, int CatalogVersion, string Name, string Description, string Target, int MaxUses, string[] Windows, Dictionary<string, decimal> Parameters);

    private sealed record RoomMemberRow(string? AbilityId, string? Ability, string? ReadyCharacterId);

    [PostgresFact]
    public async Task Guests_and_nft_holders_are_offered_the_same_abilities()
    {
        var holder = await HolderAsync("Bob");
        var (code, alice, bob) = await PairAsync(bob: holder);
        using (alice)
        using (bob)
        {
            var guestOptions = (await ViewAsync(alice, code)).Abilities;
            var holderOptions = (await ViewAsync(bob, code)).Abilities;

            Assert.Equal(
                [
                    new AbilityDto("reward_boost", "Reward Boost", "Raise one reward.", "None", 1),
                    new AbilityDto("event_reroll", "Lucky Reroll", "Reroll one event.", "None", 1),
                ],
                guestOptions);
            Assert.Equal(guestOptions, holderOptions);
        }
    }

    [PostgresFact]
    public async Task A_whale_is_offered_the_same_abilities_as_a_guest_and_others_see_only_the_badge()
    {
        var whale = await HolderAsync("Bob", others: 11);
        (await whale.GetAsync("/api/characters")).Dispose(); // the gallery read that records the holdings (DEC-065)
        var (code, alice, bob) = await PairAsync(bob: whale);
        using (alice)
        using (bob)
        {
            var guestView = await ViewAsync(alice, code);
            var whaleView = await ViewAsync(bob, code);
            var raw = await alice.GetStringAsync($"/api/rooms/{code}");

            Assert.Equal(guestView.Abilities, whaleView.Abilities);
            Assert.Equal(2, guestView.Abilities.Length);
            Assert.Equal(new BadgeDto("whale", "Whale"), guestView.Member("Bob").Collector);
            Assert.Null(guestView.Member("Alice").Collector);
            Assert.DoesNotContain("chogsHeld", raw, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("wallet", raw, StringComparison.OrdinalIgnoreCase);
        }
    }

    [PostgresFact]
    public async Task A_player_cannot_be_ready_without_an_ability()
    {
        var (code, alice, bob) = await PairAsync();
        using (alice)
        using (bob)
        {
            var before = await ViewAsync(bob, code);

            await ProblemAsync(await ReadyAsync(bob, code), HttpStatusCode.Conflict, "ability_required");

            var after = await ViewAsync(bob, code);
            Assert.Equal((before.Version, false), (after.Version, after.Member("Bob").IsReady));
        }
    }

    [PostgresFact]
    public async Task A_player_with_an_ability_can_be_ready_and_the_host_can_start()
    {
        var (code, alice, bob) = await PairAsync();
        using (alice)
        using (bob)
        {
            await ChosenAsync(alice, code, "reward_boost");
            await ChosenAsync(bob, code, "event_reroll");
            await ReadiedAsync(alice, code);
            Assert.True((await ReadiedAsync(bob, code)).Member("Bob").IsReady);

            using var started = await StartAsync(alice, code);
            var room = await RoomAsync(started);

            Assert.Equal("Active", room.Status);
            Assert.Equal(("reward_boost", "event_reroll"), (room.Member("Alice").Ability!.Id, room.Member("Bob").Ability!.Id));
            Assert.Empty(room.Abilities);
        }
    }

    [PostgresTheory]
    [InlineData("teleport")]
    [InlineData("Reward_Boost")]
    [InlineData("reward_boost ")]
    [InlineData("")]
    [InlineData("x'; DROP TABLE \"RoomMembers\"; --")]
    public async Task An_unknown_ability_is_refused_and_changes_nothing(string abilityId)
    {
        var (code, alice, bob) = await PairAsync();
        using (alice)
        using (bob)
        {
            await ChosenAsync(bob, code, "event_reroll");
            await ReadiedAsync(bob, code);
            var before = await ViewAsync(bob, code);

            await ProblemAsync(await ChooseAsync(bob, code, abilityId), HttpStatusCode.BadRequest, "unknown_ability");

            var after = await ViewAsync(bob, code);
            Assert.Equal(before.Version, after.Version);
            Assert.Equal((true, "event_reroll"), (after.Member("Bob").IsReady, after.Member("Bob").Ability!.Id));
        }
    }

    [PostgresTheory]
    [InlineData("{}")]
    [InlineData("{\"abilityId\":null}")]
    [InlineData("{\"abilityId\":5}")]
    public async Task Choosing_needs_an_ability_id(string body)
    {
        var (code, alice, bob) = await PairAsync();
        using (alice)
        using (bob)
        {
            using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
            using var response = await bob.PutAsync($"/api/rooms/{code}/ability", content);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.False((await ViewAsync(bob, code)).Member("Bob").HasAbility);
        }
    }

    [PostgresFact]
    public async Task A_choice_is_hidden_from_the_others_until_the_start()
    {
        var (code, alice, bob) = await PairAsync();
        using (alice)
        using (bob)
        {
            var bobsView = await ChosenAsync(bob, code, "event_reroll");
            var alicesView = await ViewAsync(alice, code);

            Assert.Equal(("event_reroll", true), (bobsView.Member("Bob").Ability!.Id, bobsView.Member("Bob").HasAbility));
            Assert.Equal((null, true), (alicesView.Member("Bob").Ability, alicesView.Member("Bob").HasAbility));
            Assert.Equal((null, false), (alicesView.Member("Alice").Ability, alicesView.Member("Alice").HasAbility));
            Assert.Equal((null, false), (bobsView.Member("Alice").Ability, bobsView.Member("Alice").HasAbility));

            await ChosenAsync(alice, code, "reward_boost");
            await ReadiedAsync(alice, code);
            await ReadiedAsync(bob, code);
            (await StartAsync(alice, code)).Dispose();

            var startedForAlice = await ViewAsync(alice, code);
            Assert.Equal("event_reroll", startedForAlice.Member("Bob").Ability!.Id);
            Assert.Equal("reward_boost", (await ViewAsync(bob, code)).Member("Alice").Ability!.Id);
        }
    }

    [PostgresFact]
    public async Task Choosing_another_ability_makes_the_player_not_ready_and_the_same_one_changes_nothing()
    {
        var (code, alice, bob) = await PairAsync();
        using (alice)
        using (bob)
        {
            await ChosenAsync(bob, code, "event_reroll");
            var ready = await ReadiedAsync(bob, code);

            var same = await ChosenAsync(bob, code, "event_reroll");
            Assert.Equal((ready.Version, true), (same.Version, same.Member("Bob").IsReady));

            var switched = await ChosenAsync(bob, code, "reward_boost");
            Assert.Equal((false, "reward_boost"), (switched.Member("Bob").IsReady, switched.Member("Bob").Ability!.Id));
            Assert.False((await ViewAsync(alice, code)).Member("Bob").IsReady);
        }
    }

    [PostgresFact]
    public async Task A_choice_is_kept_when_withdrawing_ready()
    {
        var (code, alice, bob) = await PairAsync();
        using (alice)
        using (bob)
        {
            await ChosenAsync(bob, code, "event_reroll");
            await ReadiedAsync(bob, code);

            using var notReady = await ReadyAsync(bob, code, ready: false);
            var room = await RoomAsync(notReady);

            Assert.Equal((false, "event_reroll"), (room.Member("Bob").IsReady, room.Member("Bob").Ability!.Id));
            Assert.True((await ReadiedAsync(bob, code)).Member("Bob").IsReady);
        }
    }

    [PostgresFact]
    public async Task The_start_locks_each_ability_with_its_catalog_version_and_it_cannot_be_switched()
    {
        var (code, alice, bob) = await PairAsync();
        using (alice)
        using (bob)
        {
            await ChosenAsync(alice, code, "reward_boost");
            await ChosenAsync(bob, code, "event_reroll");
            await ReadiedAsync(alice, code);
            await ReadiedAsync(bob, code);
            (await StartAsync(alice, code)).Dispose();
            var started = await ViewAsync(alice, code);

            await ProblemAsync(await ChooseAsync(alice, code, "event_reroll"), HttpStatusCode.Conflict, "room_started");

            var after = await ViewAsync(alice, code);
            Assert.Equal((started.Version, "reward_boost"), (after.Version, after.Member("Alice").Ability!.Id));
            var stored = await StoredAbilityAsync(code, "Alice");
            Assert.Equal(
                ("reward_boost", 1, "Reward Boost", "Raise one reward.", "None", 1),
                (stored.Id, stored.CatalogVersion, stored.Name, stored.Description, stored.Target, stored.MaxUses));
            Assert.Equal(["before_reward"], stored.Windows);
            Assert.Equal(new Dictionary<string, decimal> { ["percent"] = 25 }, stored.Parameters);
        }
    }

    [PostgresFact]
    public async Task A_running_match_keeps_its_abilities_when_the_catalog_changes()
    {
        var (code, alice, bob) = await PairAsync();
        var aliceSession = SessionOf(alice);
        using (alice)
        using (bob)
        {
            await ChosenAsync(alice, code, "reward_boost");
            await ChosenAsync(bob, code, "event_reroll");
            await ReadiedAsync(alice, code);
            await ReadiedAsync(bob, code);
            (await StartAsync(alice, code)).Dispose();
        }

        await RedeployAsync(Catalog(2, Boost(percent: 50)));
        using var aliceAgain = Browser(aliceSession);

        var room = await ViewAsync(aliceAgain, code);
        Assert.Equal(("Raise one reward.", "event_reroll"), (room.Member("Alice").Ability!.Description, room.Member("Bob").Ability!.Id));
        var stored = await StoredAbilityAsync(code, "Alice");
        Assert.Equal((1, 25m), (stored.CatalogVersion, stored.Parameters["percent"]));
    }

    [PostgresFact]
    public async Task An_ability_no_longer_offered_at_start_refuses_it_and_only_its_player_chooses_again()
    {
        var (code, alice, bob) = await PairAsync();
        var (aliceSession, bobSession) = (SessionOf(alice), SessionOf(bob));
        using (alice)
        using (bob)
        {
            await ChosenAsync(alice, code, "reward_boost");
            await ChosenAsync(bob, code, "event_reroll");
            await ReadiedAsync(alice, code);
            await ReadiedAsync(bob, code);
        }

        // A deploy drops Lucky Reroll while the room waits.
        await RedeployAsync(Catalog(2, Boost()));
        using var host = Browser(aliceSession);
        using var guest = Browser(bobSession);

        await ProblemAsync(await StartAsync(host, code), HttpStatusCode.Conflict, "ability_unavailable");

        var room = await ViewAsync(host, code);
        Assert.Equal("Waiting", room.Status);
        Assert.Equal((true, "reward_boost"), (room.Member("Alice").IsReady, room.Member("Alice").Ability!.Id));
        Assert.Equal((false, false), (room.Member("Bob").IsReady, room.Member("Bob").HasAbility));
        Assert.Null((await StoredMemberAsync(code, "Bob")).AbilityId);

        await ChosenAsync(guest, code, "reward_boost");
        await ReadiedAsync(guest, code);
        using var started = await StartAsync(host, code);
        Assert.Equal("Active", (await RoomAsync(started)).Status);
        var stored = await StoredAbilityAsync(code, "Bob");
        Assert.Equal(("reward_boost", 2), (stored.Id, stored.CatalogVersion));
    }

    [PostgresFact]
    public async Task A_switch_racing_the_start_either_lands_first_or_is_refused()
    {
        for (var round = 0; round < 5; round++)
        {
            var (code, alice, bob) = await PairAsync();
            using (alice)
            using (bob)
            {
                await ChosenAsync(alice, code, "reward_boost");
                await ChosenAsync(bob, code, "event_reroll");
                await ReadiedAsync(alice, code);
                await ReadiedAsync(bob, code);

                var start = StartAsync(alice, code);
                var change = ChooseAsync(bob, code, "reward_boost");
                using var started = await start;
                using var changed = await change;

                var room = await ViewAsync(alice, code);
                var stored = await StoredMemberAsync(code, "Bob");
                if (started.StatusCode == HttpStatusCode.OK)
                {
                    // The start came first: Bob's ready choice is locked and the switch is refused.
                    await ProblemAsync(changed, HttpStatusCode.Conflict, "room_started");
                    Assert.Equal(("Active", "event_reroll"), (room.Status, room.Member("Bob").Ability!.Id));
                }
                else
                {
                    // The switch came first: Bob is no longer ready, so nothing started.
                    await ProblemAsync(started, HttpStatusCode.Conflict, "players_not_ready");
                    Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
                    Assert.Equal(("Waiting", "reward_boost", null), (room.Status, stored.AbilityId, stored.Ability));
                }

                (await alice.PostAsync($"/api/rooms/{code}/leave", null)).Dispose();
                (await bob.PostAsync($"/api/rooms/{code}/leave", null)).Dispose();
            }
        }
    }

    [PostgresFact]
    public async Task Outsiders_and_unknown_rooms_get_room_not_found()
    {
        var (code, alice, bob) = await PairAsync();
        using (alice)
        using (bob)
        using (var mallory = await GuestAsync("Mallory"))
        {
            var before = await ViewAsync(alice, code);

            await ProblemAsync(await ChooseAsync(mallory, code, "reward_boost"), HttpStatusCode.NotFound, "room_not_found");
            await ProblemAsync(await ChooseAsync(mallory, "ZZZZZZ", "reward_boost"), HttpStatusCode.NotFound, "room_not_found");
            await ProblemAsync(await ChooseAsync(mallory, "not-a-code", "reward_boost"), HttpStatusCode.NotFound, "room_not_found");

            Assert.Equal(before.Version, (await ViewAsync(alice, code)).Version);
        }
    }

    [PostgresFact]
    public async Task Choosing_needs_a_session()
    {
        var (code, alice, bob) = await PairAsync();
        using (alice)
        using (bob)
        using (var anonymous = Browser())
        {
            using var response = await ChooseAsync(anonymous, code, "reward_boost");

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    [PostgresFact]
    public async Task A_game_without_abilities_readies_and_starts_without_one()
    {
        var (code, alice, bob) = await PairAsync("chog-race");
        using (alice)
        using (bob)
        {
            Assert.Empty((await ViewAsync(bob, code)).Abilities);
            await ProblemAsync(await ChooseAsync(bob, code, "reward_boost"), HttpStatusCode.BadRequest, "unknown_ability");

            await ReadiedAsync(alice, code);
            await ReadiedAsync(bob, code);
            using var started = await StartAsync(alice, code);

            var room = await RoomAsync(started);
            Assert.Equal("Active", room.Status);
            Assert.All(room.Members, m => Assert.Equal((null, false), (m.Ability, m.HasAbility)));
        }
    }

    private static string SessionOf(HttpClient client) => client.DefaultRequestHeaders.GetValues("Cookie").Single();

    /// <summary>Stops the server and starts one with <paramref name="catalog"/> on the same database, as a deploy would.</summary>
    private async Task RedeployAsync(AbilityCatalog catalog)
    {
        await _factory.DisposeAsync();
        _factory = Factory(catalog);
        _factory.CreateClient().Dispose();
    }

    /// <summary>STORY-061, DEC-066: Legendary Chogs may only reroll in Chog Life, with trait-based abilities on or off.</summary>
    private static Dictionary<string, string?> LegendaryRerolls(bool enabled) => new()
    {
        ["Abilities:TraitBased:Enabled"] = enabled ? "true" : "false",
        ["Abilities:TraitBased:Rules:0:Game"] = "chog-life",
        ["Abilities:TraitBased:Rules:0:Traits:0:Type"] = "Rarity",
        ["Abilities:TraitBased:Rules:0:Traits:0:Value"] = "Legendary",
        ["Abilities:TraitBased:Rules:0:Abilities:0"] = "event_reroll",
    };

    private async Task UseTraitRulesAsync(bool enabled)
    {
        await _factory.DisposeAsync();
        _factory = Factory(Catalog(1, Boost(), Reroll()), LegendaryRerolls(enabled));
    }

    /// <summary>What the metadata cache stored for token 7 while <paramref name="owner"/> held it (STORY-016).</summary>
    private async Task CacheTraitsAsync(WalletAddress owner, string type, string value)
    {
        await using var db = database.CreateDbContext();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "ChogNfts" ("ChainId", "ContractAddress", "TokenId", "OwnerAddress", "OwnerRefreshedAt", "MetadataStatus")
            VALUES (143, '0xc96d31f8626c6d03fae5dcd3d61e3fb9f4a73763', 7, {owner.Value}, now(), 'Ok');
            INSERT INTO "ChogNftTraits" ("ChainId", "ContractAddress", "TokenId", "Position", "TraitType", "TraitValue")
            VALUES (143, '0xc96d31f8626c6d03fae5dcd3d61e3fb9f4a73763', 7, 0, {type}, {value});
            """);
    }

    [PostgresFact]
    public async Task With_trait_rules_off_a_legendary_holder_is_offered_what_a_guest_is()
    {
        await UseTraitRulesAsync(enabled: false);
        var (holder, wallet) = await HolderWithWalletAsync("Bob");
        await CacheTraitsAsync(wallet, "Rarity", "Legendary");
        var (code, alice, bob) = await PairAsync(bob: holder);
        using (alice)
        using (bob)
        {
            Assert.Equal(["reward_boost", "event_reroll"], (await ViewAsync(bob, code)).Abilities.Select(a => a.Id));
            Assert.Equal((await ViewAsync(alice, code)).Abilities, (await ViewAsync(bob, code)).Abilities);
            await ChosenAsync(bob, code, "reward_boost");
        }
    }

    [PostgresFact]
    public async Task With_trait_rules_on_a_legendary_holder_gets_their_rule_and_a_guest_keeps_the_whole_pool()
    {
        await UseTraitRulesAsync(enabled: true);
        var (holder, wallet) = await HolderWithWalletAsync("Bob");
        await CacheTraitsAsync(wallet, " rarity ", "LEGENDARY");
        var (code, alice, bob) = await PairAsync(bob: holder);
        using (alice)
        using (bob)
        {
            Assert.Equal(["reward_boost", "event_reroll"], (await ViewAsync(alice, code)).Abilities.Select(a => a.Id));
            Assert.Equal(["event_reroll"], (await ViewAsync(bob, code)).Abilities.Select(a => a.Id));

            var before = await ViewAsync(bob, code);
            await ProblemAsync(await ChooseAsync(bob, code, "reward_boost"), HttpStatusCode.BadRequest, "ability_unavailable");
            var after = await ViewAsync(bob, code);
            Assert.Equal((before.Version, false), (after.Version, after.Member("Bob").HasAbility));

            await ChosenAsync(alice, code, "reward_boost");
            await ChosenAsync(bob, code, "event_reroll");
            await ReadiedAsync(alice, code);
            await ReadiedAsync(bob, code);
            using var started = await StartAsync(alice, code);
            var room = await RoomAsync(started);

            Assert.Equal(("reward_boost", "event_reroll"), (room.Member("Alice").Ability!.Id, room.Member("Bob").Ability!.Id));
        }
    }

    [PostgresFact]
    public async Task With_trait_rules_on_the_live_traits_at_start_decide_over_the_cache()
    {
        await UseTraitRulesAsync(enabled: true);
        var (code, alice, bob) = await PairAsync(bob: await HolderAsync("Bob"));
        using (alice)
        using (bob)
        {
            // Nothing cached yet: the lobby offers the whole pool, but token 7 is Legendary on chain.
            Assert.Equal(["reward_boost", "event_reroll"], (await ViewAsync(bob, code)).Abilities.Select(a => a.Id));
            await ChosenAsync(alice, code, "reward_boost");
            await ChosenAsync(bob, code, "reward_boost");
            await ReadiedAsync(alice, code);
            await ReadiedAsync(bob, code);

            await ProblemAsync(await StartAsync(alice, code), HttpStatusCode.Conflict, "ability_unavailable");

            var after = await ViewAsync(bob, code);
            Assert.Equal("Waiting", after.Status);
            Assert.Equal((false, false), (after.Member("Bob").IsReady, after.Member("Bob").HasAbility));
            Assert.Equal((true, true), (after.Member("Alice").IsReady, after.Member("Alice").HasAbility));
        }
    }

    [PostgresFact]
    public async Task With_trait_rules_on_cached_traits_of_a_token_the_wallet_no_longer_holds_are_ignored()
    {
        await UseTraitRulesAsync(enabled: true);
        var (holder, _) = await HolderWithWalletAsync("Bob");
        await CacheTraitsAsync(TestWallet.Random().Address, "Rarity", "Legendary");
        var (code, alice, bob) = await PairAsync(bob: holder);
        using (alice)
        using (bob)
        {
            Assert.Equal(["reward_boost", "event_reroll"], (await ViewAsync(bob, code)).Abilities.Select(a => a.Id));
        }
    }

    /// <summary>The shipped holder rules (DEC-077) with Chog Life's real catalog, or with <paramref name="race"/> Chog Race's.</summary>
    private async Task UseHolderRulesAsync(IReadOnlyDictionary<string, string?>? settings = null, string game = "chog-life")
    {
        await _factory.DisposeAsync();
        _factory = new ApiFactory(
            database.ConnectionString,
            new Dictionary<string, string?>(settings ?? new Dictionary<string, string?>())
            {
                [game switch { "chog-race" => "Games:Items:1:Status", "chog-royale" => "Games:Items:2:Status", _ => "Games:Items:0:Status" }] = "Available",
            },
            services =>
            {
                services.AddScoped<INftProvider>(_ => _chain);
                _ = game switch
                {
                    "chog-race" => services.AddChogRaceAbilities(),
                    "chog-royale" => services.AddChogRoyaleAbilities(),
                    _ => services.AddChogLifeAbilities(),
                };
            },
            holderAbilities: true);
    }

    [PostgresFact]
    public async Task A_chog_royale_guest_gets_the_two_standard_abilities_and_a_legendary_holder_all_six_with_legendary_values()
    {
        // STORY-093, DEC-095 (revising STORY-084's parity, DEC-091): the shipped holder rules split Chog Royale as the other games.
        await UseHolderRulesAsync(game: "chog-royale");
        var (code, alice, bob) = await PairAsync("chog-royale", await TierHolderAsync("Bob", "Legendary"));
        using (alice)
        using (bob)
        {
            Assert.Equal(
                ["diamond_hands_plus: blocks=1 x1", "stop_loss: mon=5 x1"],
                Described((await RawAsync(alice, code)).GetProperty("abilities").EnumerateArray()));
            Assert.Equal(
                ["alpha_leak:  x1", "double_pump: percent=50 x1", "fresh_mint:  x1", "insider_info:  x1"],
                Described((await RawAsync(alice, code)).GetProperty("unavailableAbilities").EnumerateArray()));
            Assert.All((await RawAsync(alice, code)).GetProperty("unavailableAbilities").EnumerateArray(), a => Assert.True(a.GetProperty("holderOnly").GetBoolean()));
            var holders = (await RawAsync(bob, code)).GetProperty("abilities").EnumerateArray().ToArray();
            Assert.Equal(
                [
                    "diamond_hands_plus: blocks=3 x1", "alpha_leak:  x2", "double_pump: percent=100 x1", "stop_loss: mon=0 x1",
                    "fresh_mint:  x2", "insider_info:  x2",
                ],
                Described(holders));
            Assert.Equal([false, true, true, false, true, true], holders.Select(a => a.GetProperty("holderOnly").GetBoolean()));
            Assert.Empty((await RawAsync(bob, code)).GetProperty("unavailableAbilities").EnumerateArray());

            var before = await ViewAsync(alice, code);
            foreach (var holderOnly in new[] { "alpha_leak", "double_pump", "fresh_mint", "insider_info" })
            {
                await ProblemAsync(await ChooseAsync(alice, code, holderOnly), HttpStatusCode.BadRequest, "ability_unavailable");
            }

            Assert.Equal(before.Version, (await ViewAsync(alice, code)).Version);

            await ChosenAsync(alice, code, "stop_loss");
            await ChosenAsync(bob, code, "insider_info");
            await ReadiedAsync(alice, code);
            await ReadiedAsync(bob, code);
            using var started = await StartAsync(alice, code);
            Assert.Equal(HttpStatusCode.OK, started.StatusCode);

            // The abilities differ; the start doesn't: 100 MON and a hand of three each.
            var room = await RawAsync(bob, code);
            Assert.Equal(["stop_loss: mon=5 x1"], Described([MemberAbility(room, "Alice")]));
            Assert.Equal(["insider_info:  x2"], Described([MemberAbility(room, "Bob")]));
            Assert.EndsWith("2 uses per game, one a round.", MemberAbility(room, "Bob").GetProperty("description").GetString());
            Assert.True(MemberAbility(room, "Bob").GetProperty("holderOnly").GetBoolean());
            Assert.All(room.GetProperty("state").GetProperty("players").EnumerateArray(), p => Assert.Equal((100, 3), (p.GetProperty("mon").GetInt32(), p.GetProperty("cards").GetInt32())));
        }
    }

    [PostgresFact]
    public async Task A_chog_race_guest_cant_choose_double_dice_or_freeze_and_an_epic_holder_gets_them_with_epic_values()
    {
        await UseHolderRulesAsync(game: "chog-race");
        var (code, alice, bob) = await PairAsync("chog-race", await TierHolderAsync("Bob", "Epic"));
        using (alice)
        using (bob)
        {
            Assert.Equal(
                ["attack_block: blocks=1 x1", "card_preview: cards=1 x1"],
                Described((await RawAsync(alice, code)).GetProperty("abilities").EnumerateArray()));
            Assert.Equal(
                [
                    "movement_boost: tiles=4 x1", "attack_block: blocks=2 x1", "card_preview: cards=2 x1", "card_steal: cards=2 x1",
                    "double_dice: dice=3 x1", "freeze:  x2",
                ],
                Described((await RawAsync(bob, code)).GetProperty("abilities").EnumerateArray()));
            Assert.Equal(
                ["movement_boost: tiles=2 x1", "card_steal: cards=1 x1", "double_dice: dice=2 x1", "freeze:  x1"],
                Described((await RawAsync(alice, code)).GetProperty("unavailableAbilities").EnumerateArray()));
            Assert.All((await RawAsync(alice, code)).GetProperty("unavailableAbilities").EnumerateArray(), a => Assert.True(a.GetProperty("holderOnly").GetBoolean()));

            var before = await ViewAsync(alice, code);
            await ProblemAsync(await ChooseAsync(alice, code, "double_dice"), HttpStatusCode.BadRequest, "ability_unavailable");
            await ProblemAsync(await ChooseAsync(alice, code, "freeze"), HttpStatusCode.BadRequest, "ability_unavailable");
            Assert.Equal(before.Version, (await ViewAsync(alice, code)).Version);

            await ChosenAsync(alice, code, "attack_block");
            await ChosenAsync(bob, code, "freeze");
            await ReadiedAsync(alice, code);
            await ReadiedAsync(bob, code);
            using var started = await StartAsync(alice, code);
            Assert.Equal(HttpStatusCode.OK, started.StatusCode);
            var room = await RawAsync(alice, code);
            Assert.Equal(["freeze:  x2"], Described([MemberAbility(room, "Bob")]));
            Assert.EndsWith("2 uses per game, one a turn.", MemberAbility(room, "Bob").GetProperty("description").GetString());
            Assert.True(MemberAbility(room, "Bob").GetProperty("holderOnly").GetBoolean());
        }
    }

    /// <summary>A holder of token 7 with <paramref name="tier"/> on chain and, when given, <paramref name="cached"/> in the cache.</summary>
    private async Task<HttpClient> TierHolderAsync(string name, string tier, string? cached = null)
    {
        var (client, wallet) = await HolderWithWalletAsync(name, traits: [new NftTrait("Tier", tier)]);
        await CacheTraitsAsync(wallet, "Tier", cached ?? tier);
        return client;
    }

    /// <summary>Each offered or locked ability as "id: values, uses".</summary>
    private static string[] Described(IEnumerable<JsonElement> abilities) =>
    [
        .. abilities.Select(a =>
            $"{a.GetProperty("id").GetString()}: "
            + string.Join(",", a.GetProperty("values").EnumerateObject().Select(v => $"{v.Name}={v.Value.GetDecimal()}"))
            + $" x{a.GetProperty("maxUses").GetInt32()}"),
    ];

    private static async Task<JsonElement> RawAsync(HttpClient client, string code) =>
        JsonDocument.Parse(await client.GetStringAsync($"/api/rooms/{code}")).RootElement.Clone();

    private static JsonElement MemberAbility(JsonElement room, string name) =>
        room.GetProperty("members").EnumerateArray().Single(m => m.GetProperty("displayName").GetString() == name).GetProperty("ability");

    [PostgresFact]
    public async Task A_guest_gets_the_standard_abilities_and_an_epic_holder_all_of_them_with_epic_values()
    {
        await UseHolderRulesAsync();
        var (code, alice, bob) = await PairAsync(bob: await TierHolderAsync("Bob", "Epic"));
        using (alice)
        using (bob)
        {
            Assert.Equal(
                ["reward_boost: percent=25 x1", "loss_protection: percent=50 x1"],
                Described((await RawAsync(alice, code)).GetProperty("abilities").EnumerateArray()));
            Assert.Equal(
                [
                    "reward_boost: percent=40 x1", "loss_protection: percent=35 x1", "event_preview:  x2", "event_reroll: draws=3 x1",
                    "double_dice: dice=3 x1", "tax_the_rich: cap=50,percent=18 x1",
                ],
                Described((await RawAsync(bob, code)).GetProperty("abilities").EnumerateArray()));
            Assert.Contains("earns 40% more", (await RawAsync(bob, code)).GetProperty("abilities")[0].GetProperty("description").GetString());
            // Holder-only abilities are marked (STORY-091, DEC-078).
            Assert.Equal(
                [false, false, true, true, true, true],
                (await RawAsync(bob, code)).GetProperty("abilities").EnumerateArray().Select(a => a.GetProperty("holderOnly").GetBoolean()));
            Assert.All((await RawAsync(alice, code)).GetProperty("abilities").EnumerateArray(), a => Assert.False(a.GetProperty("holderOnly").GetBoolean()));

            // The guest sees the holder-only abilities locked, with Base values; the holder has none locked.
            Assert.Equal(
                [
                    "event_preview:  x1", "event_reroll: draws=2 x1", "double_dice: dice=2 x1", "tax_the_rich: cap=50,percent=10 x1",
                ],
                Described((await RawAsync(alice, code)).GetProperty("unavailableAbilities").EnumerateArray()));
            Assert.All((await RawAsync(alice, code)).GetProperty("unavailableAbilities").EnumerateArray(), a => Assert.True(a.GetProperty("holderOnly").GetBoolean()));
            Assert.Empty((await RawAsync(bob, code)).GetProperty("unavailableAbilities").EnumerateArray());

            // A holder-only ability is refused to the guest, and nothing changes.
            var before = await ViewAsync(alice, code);
            await ProblemAsync(await ChooseAsync(alice, code, "event_preview"), HttpStatusCode.BadRequest, "ability_unavailable");
            await ProblemAsync(await ChooseAsync(alice, code, "double_dice"), HttpStatusCode.BadRequest, "ability_unavailable");
            await ProblemAsync(await ChooseAsync(alice, code, "tax_the_rich"), HttpStatusCode.BadRequest, "ability_unavailable");
            await ProblemAsync(await ChooseAsync(alice, code, "fireball"), HttpStatusCode.BadRequest, "unknown_ability");
            var after = await ViewAsync(alice, code);
            Assert.Equal((before.Version, false), (after.Version, after.Member("Alice").HasAbility));

            // Values and traits never come from a request: extra fields change nothing.
            using (var forged = await alice.PutAsJsonAsync(
                $"/api/rooms/{code}/ability", new { abilityId = "reward_boost", values = new { percent = 100 }, tier = "Legendary", maxUses = 5 }))
            {
                Assert.Equal(HttpStatusCode.OK, forged.StatusCode);
            }

            Assert.Equal(["reward_boost: percent=25 x1"], Described([MemberAbility(await RawAsync(alice, code), "Alice")]));
            var chosen = await ChosenAsync(bob, code, "event_preview");
            Assert.Equal(2, chosen.Member("Bob").Ability!.MaxUses);
            await ReadiedAsync(alice, code);
            await ReadiedAsync(bob, code);
            using var started = await StartAsync(alice, code);
            Assert.Equal(HttpStatusCode.OK, started.StatusCode);

            var room = await RawAsync(alice, code);
            Assert.Equal(["reward_boost: percent=25 x1"], Described([MemberAbility(room, "Alice")]));
            Assert.Equal(["event_preview:  x2"], Described([MemberAbility(room, "Bob")]));
            Assert.Equal(2, MemberAbility(room, "Bob").GetProperty("usesLeft").GetInt32());
            Assert.Equal((false, true), (MemberAbility(room, "Alice").GetProperty("holderOnly").GetBoolean(), MemberAbility(room, "Bob").GetProperty("holderOnly").GetBoolean()));
            Assert.Empty(room.GetProperty("unavailableAbilities").EnumerateArray());
            Assert.EndsWith("2 uses per game.", MemberAbility(room, "Bob").GetProperty("description").GetString());
            var stored = await StoredAbilityAsync(code, "Bob");
            Assert.Equal((LifeAbilities.Version, 2), (stored.CatalogVersion, stored.MaxUses));
        }
    }

    [PostgresFact]
    public async Task A_common_holder_gets_every_ability_with_base_values()
    {
        await UseHolderRulesAsync();
        var (code, alice, bob) = await PairAsync(bob: await TierHolderAsync("Bob", "Common"));
        using (alice)
        using (bob)
        {
            Assert.Equal(
                [
                    "reward_boost: percent=25 x1", "loss_protection: percent=50 x1", "event_preview:  x1", "event_reroll: draws=2 x1",
                    "double_dice: dice=2 x1", "tax_the_rich: cap=50,percent=10 x1",
                ],
                Described((await RawAsync(bob, code)).GetProperty("abilities").EnumerateArray()));
        }
    }

    [PostgresFact]
    public async Task The_live_tier_at_start_locks_its_values_over_the_cached_one()
    {
        await UseHolderRulesAsync();
        var (code, alice, bob) = await PairAsync(bob: await TierHolderAsync("Bob", tier: "Rare", cached: "Legendary"));
        using (alice)
        using (bob)
        {
            Assert.Equal("reward_boost: percent=50 x1", Described((await RawAsync(bob, code)).GetProperty("abilities").EnumerateArray())[0]);
            await ChosenAsync(alice, code, "loss_protection");
            await ChosenAsync(bob, code, "reward_boost");
            await ReadiedAsync(alice, code);
            await ReadiedAsync(bob, code);
            using var started = await StartAsync(alice, code);
            Assert.Equal(HttpStatusCode.OK, started.StatusCode);

            Assert.Equal(["reward_boost: percent=35 x1"], Described([MemberAbility(await RawAsync(bob, code), "Bob")]));
            Assert.Equal(35, (await StoredAbilityAsync(code, "Bob")).Parameters["percent"]);
        }
    }

    [PostgresFact]
    public async Task A_chog_sold_before_the_start_leaves_its_player_the_standard_pool()
    {
        await UseHolderRulesAsync();
        var (holder, wallet) = await HolderWithWalletAsync("Bob", traits: [new NftTrait("Tier", "Rare")]);
        var (code, alice, bob) = await PairAsync(bob: holder);
        using (alice)
        using (bob)
        {
            await ChosenAsync(alice, code, "reward_boost");
            await ChosenAsync(bob, code, "event_reroll");
            await ReadiedAsync(alice, code);
            await ReadiedAsync(bob, code);

            _chain.Holdings[wallet] = [];
            await ProblemAsync(await StartAsync(alice, code), HttpStatusCode.Conflict, "character_unavailable");

            using var standard = await bob.PutAsJsonAsync("/api/characters/selected", new { kind = "Standard", id = "mint" });
            Assert.Equal(HttpStatusCode.OK, standard.StatusCode);
            Assert.Equal(["reward_boost", "loss_protection"], (await ViewAsync(bob, code)).Abilities.Select(a => a.Id));
            await ProblemAsync(await ReadyAsync(bob, code), HttpStatusCode.Conflict, "ability_required");

            await ChosenAsync(bob, code, "loss_protection");
            await ReadiedAsync(bob, code);
            using var started = await StartAsync(alice, code);
            Assert.Equal(HttpStatusCode.OK, started.StatusCode);
            Assert.Equal(["loss_protection: percent=50 x1"], Described([MemberAbility(await RawAsync(bob, code), "Bob")]));
        }
    }

    [PostgresFact]
    public async Task A_config_change_never_alters_a_running_match()
    {
        await UseHolderRulesAsync();
        var (code, alice, bob) = await PairAsync(bob: await TierHolderAsync("Bob", "Epic"));
        var (aliceCookie, bobCookie) = (Cookie(alice), Cookie(bob));
        using (alice)
        using (bob)
        {
            await ChosenAsync(alice, code, "reward_boost");
            await ChosenAsync(bob, code, "event_preview");
            await ReadiedAsync(alice, code);
            await ReadiedAsync(bob, code);
            using var started = await StartAsync(alice, code);
            Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        }

        // A redeploy with retuned Epic and Base values.
        await UseHolderRulesAsync(new Dictionary<string, string?>
        {
            ["Abilities:Holder:Games:chog-life:Tiers:Epic:event_preview:uses"] = "3",
            ["Abilities:Holder:Games:chog-life:Tiers:Base:reward_boost:percent"] = "30",
        });
        using var aliceAgain = Browser(aliceCookie);
        using var bobAgain = Browser(bobCookie);
        var room = await RawAsync(bobAgain, code);

        Assert.Equal(["event_preview:  x2"], Described([MemberAbility(room, "Bob")]));
        Assert.Equal(["reward_boost: percent=25 x1"], Described([MemberAbility(room, "Alice")]));
    }

    private static string Cookie(HttpClient client) => client.DefaultRequestHeaders.GetValues("Cookie").Single();

    private sealed class NoEffect(string abilityId, IEnumerable<string> parameters) : IAbilityHandler
    {
        public string AbilityId => abilityId;

        public IReadOnlyList<AbilityParameter> Parameters { get; } = [.. parameters.Select(p => new AbilityParameter(p, 1, 100))];

        public string? CanExecute(AbilityContext context) => null;

        public JsonElement Execute(AbilityContext context) => context.State!.Value;
    }

    private sealed class NoWindow : IAbilityTiming
    {
        public string? CurrentWindow(AbilityContext context) => null;
    }

    /// <summary>Chain stand-in: holdings per wallet.</summary>
    private sealed class FakeNftProvider : INftProvider
    {
        public System.Collections.Concurrent.ConcurrentDictionary<WalletAddress, List<OwnedNft>> Holdings { get; } = new();

        public Task<IReadOnlyList<OwnedNft>> GetOwnedAsync(WalletAddress owner, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<OwnedNft>>(Holdings.GetValueOrDefault(owner) ?? []);

        public Task<OwnedNft?> FindOwnedAsync(WalletAddress owner, NftTokenId tokenId, CancellationToken cancellationToken) =>
            Task.FromResult((Holdings.GetValueOrDefault(owner) ?? []).FirstOrDefault(n => n.TokenId == tokenId));
    }
}
