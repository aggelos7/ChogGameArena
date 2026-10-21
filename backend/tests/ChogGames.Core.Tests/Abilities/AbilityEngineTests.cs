using System.Reflection;
using System.Text.Json;
using ChogGames.Core.Abilities;
using ChogGames.Core.Games;
using ChogGames.Core.Matches;
using ChogGames.Core.Nfts;
using ChogGames.Core.Players;
using ChogGames.Core.Rooms;
using ChogGames.Core.Wallets;
using static ChogGames.Core.Tests.Abilities.FakeAbilityGame;

namespace ChogGames.Core.Tests.Abilities;

public class AbilityEngineTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

    private readonly BoostHandler _boost = new();
    private readonly StealHandler _steal = new();
    private readonly AbilityEngine _engine;

    public AbilityEngineTests() => _engine = new AbilityEngine([Catalog(_boost, _steal)]);

    private static Player Guest(string name)
    {
        Assert.True(PlayerDisplayName.TryCreate(name, out var displayName));
        return Player.CreateGuest(displayName, Now);
    }

    private static CharacterSnapshot NftChog(params NftTrait[] traits)
    {
        Assert.True(WalletAddress.TryParse("0x" + new string('a', 40), out var wallet));
        return CharacterSnapshot.Nft(wallet, new OwnedNft(NftTokenId.From(7), "Chog #7", null, traits), Now);
    }

    /// <summary>A waiting room of <paramref name="players"/>, the first hosting, the others joining in order.</summary>
    private static GameRoom Waiting(string gameType, Player[] players)
    {
        Assert.True(RoomCode.TryParse("K7QF3M", out var code));
        var room = GameRoom.Create(GameDefinition.Create(gameType, 2, 6), players[0], code, Now);
        for (var i = 1; i < players.Length; i++)
        {
            room.Join(players[i], Now.AddSeconds(i));
        }

        return room;
    }

    /// <summary>
    /// A started match seated in join order (seat 1 first); the first player plays <paramref name="hostChog"/>. In a game
    /// with a catalog everyone locks <paramref name="ability"/> from <paramref name="catalog"/> (the fake game's by default).
    /// </summary>
    private static GameRoom Started(
        Player[] players,
        CharacterSnapshot? hostChog = null,
        string gameType = GameType,
        string ability = "boost",
        AbilityCatalog? catalog = null)
    {
        var room = Waiting(gameType, players);
        catalog ??= gameType == GameType ? Catalog() : null;
        foreach (var player in players)
        {
            if (catalog is not null)
            {
                room.SelectAbility(player.Id, ability);
            }

            room.MarkReady(player);
        }

        var characters = players.ToDictionary(p => p.Id, p => CharacterSnapshot.Standard(DefaultAvatar.Classic, Now));
        characters[players[0].Id] = hostChog ?? characters[players[0].Id];
        var abilities = catalog is not null && catalog.TryFind(ability, out var definition, out _)
            ? players.ToDictionary(p => p.Id, _ => AbilitySnapshot.Lock(catalog, definition))
            : null;
        room.Start(players[0].Id, players, characters, TimeSpan.FromSeconds(20), ScriptedRandomSource.KeepOrder, Now, abilities);
        return room;
    }

    private static JsonElement Payload(string json) => JsonDocument.Parse(json).RootElement.Clone();

    /// <summary>The ability a payload names, or <c>boost</c> when it names none of the fake game's.</summary>
    private static string NamedIn(string payload) =>
        payload.Contains("\"steal\"", StringComparison.Ordinal) ? "steal" : "boost";

    private AbilityResult Use(GameRoom room, Player player, string payload, JsonElement? state, ScriptedRandomSource? random = null) =>
        _engine.Use(new AbilityRequest(room, player.Id, Payload(payload), state, random ?? new ScriptedRandomSource()));

    /// <summary>Counts the use as the command service does after the engine allowed it.</summary>
    private static void Apply(GameRoom room, Player player, AbilityResult result) =>
        room.ApplyAbility(
            player.Id,
            new MatchCommand(Guid.NewGuid(), room.Id, room.Version, MatchCommand.UseAbility, Payload("{}")),
            result.Ability!.Id,
            result.State!.Value.GetRawText(),
            Now);

    [Fact]
    public void Ability_is_used_by_id_with_its_configured_value()
    {
        Player[] players = [Guest("Alice"), Guest("Bob")];
        var room = Started(players);

        var result = Use(room, players[0], """{"abilityId":"boost"}""", State("before_reward", reward: 100));

        Assert.True(result.Used);
        Assert.Equal((Boost(), null, null), (result.Ability, result.Target, result.Reason));
        Assert.Equal(125, result.State!.Value.GetProperty("reward").GetInt32());
        Assert.Equal(1, _boost.Executions);
    }

    [Fact]
    public void Opponent_is_targeted_by_seat_and_chance_comes_from_the_server()
    {
        Player[] players = [Guest("Alice"), Guest("Bob"), Guest("Carol")];
        var room = Started(players, ability: "steal");
        var random = new ScriptedRandomSource(2);

        var result = Use(room, players[0], """{"abilityId":"steal","target":2}""", State("own_turn", targetCards: 3), random);

        Assert.True(result.Used);
        Assert.Equal(new AbilityPlayer(players[1].Id, 2, true), result.Target);
        Assert.Equal((2, 2), (result.State!.Value.GetProperty("stolen").GetInt32(), result.State.Value.GetProperty("cards").GetProperty("2").GetInt32()));
        Assert.Equal([(0, 3)], random.Draws);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("""{}""")]
    [InlineData("""{"abilityId":7}""")]
    [InlineData("""{"abilityId":"Boost"}""")]
    [InlineData("""{"abilityId":"boost","roll":6}""")]
    [InlineData("""{"abilityId":"boost","abilityId":"steal"}""")]
    [InlineData("""{"abilityId":"steal","target":"2"}""")]
    [InlineData("""{"abilityId":"steal","target":2.5}""")]
    [InlineData("""{"abilityId":"steal","target":null}""")]
    [InlineData("""{"abilityId":"steal","playerId":"00000000-0000-0000-0000-000000000001"}""")]
    public void Malformed_payload_is_refused(string payload)
    {
        AssertRefused(AbilityRefusal.InvalidPayload, payload, State("before_reward"));
    }

    [Theory]
    [InlineData("""{"abilityId":"reward_boost"}""")]
    [InlineData("""{"abilityId":"card_steal","target":2}""")]
    public void Ability_the_game_does_not_define_is_unknown(string payload)
    {
        AssertRefused(AbilityRefusal.UnknownAbility, payload, State("before_reward"));
    }

    [Fact]
    public void Game_without_a_catalog_has_no_abilities()
    {
        Player[] players = [Guest("Alice"), Guest("Bob")];
        var room = Started(players, gameType: GameDefinition.ChogLife);

        Assert.Equal(AbilityRefusal.UnknownAbility, Use(room, players[0], """{"abilityId":"boost"}""", State("before_reward")).Reason);
        AssertNothingRan();
    }

    [Theory]
    [InlineData("""{"abilityId":"steal"}""")]
    [InlineData("""{"abilityId":"steal","target":1}""")]
    [InlineData("""{"abilityId":"steal","target":3}""")]
    [InlineData("""{"abilityId":"steal","target":4}""")]
    [InlineData("""{"abilityId":"steal","target":0}""")]
    [InlineData("""{"abilityId":"steal","target":-1}""")]
    [InlineData("""{"abilityId":"boost","target":2}""")]
    public void Target_must_be_an_opponent_still_playing_and_only_for_abilities_that_take_one(string payload)
    {
        // Seat 1 is the user, seat 3 has left, seat 4 doesn't exist.
        AssertRefused(AbilityRefusal.InvalidTarget, payload, State("own_turn"), leaveSeat3: true);
    }

    [Theory]
    [InlineData("""{"abilityId":"boost"}""", "own_turn")]
    [InlineData("""{"abilityId":"steal","target":2}""", "after_reward")]
    public void Ability_outside_its_windows_is_refused(string payload, string window)
    {
        AssertRefused(AbilityRefusal.NotNow, payload, State(window));
    }

    [Fact]
    public void Ability_before_the_game_has_state_is_refused()
    {
        AssertRefused(AbilityRefusal.NotNow, """{"abilityId":"boost"}""", state: null);
    }

    [Theory]
    [InlineData("""{"abilityId":"boost"}""", "no_reward_pending")]
    [InlineData("""{"abilityId":"steal","target":2}""", "target_has_no_cards")]
    public void Game_refuses_with_its_own_reason_and_nothing_changes(string payload, string reason)
    {
        Player[] players = [Guest("Alice"), Guest("Bob")];
        var room = Started(players, ability: NamedIn(payload));
        var random = new ScriptedRandomSource();
        var version = room.Version;

        var result = Use(room, players[0], payload, State("before_reward", reward: 0, targetCards: 0), random);

        Assert.Equal((false, reason, null), (result.Used, result.Reason, result.State));
        Assert.Equal(1, _boost.Checks + _steal.Checks);
        Assert.Equal(0, _boost.Executions);
        Assert.Empty(random.Draws);
        Assert.Equal(version, room.Version);
    }

    [Fact]
    public void Only_players_still_in_an_active_match_use_abilities()
    {
        Player[] players = [Guest("Alice"), Guest("Bob"), Guest("Carol")];
        var outsider = Guest("Mallory");
        var match = Started(players);
        match.Leave(players[2].Id, Now.AddMinutes(1));
        var waiting = Waiting(GameType, [Guest("Dave"), Guest("Erin")]);
        var payload = """{"abilityId":"boost"}""";

        Assert.Equal(AbilityRefusal.NotPlaying, Use(match, outsider, payload, State("before_reward")).Reason);
        Assert.Equal(AbilityRefusal.NotPlaying, Use(match, players[2], payload, State("before_reward")).Reason);
        Assert.Equal(AbilityRefusal.NotPlaying, _engine.Use(new AbilityRequest(waiting, waiting.HostPlayerId, Payload(payload), State("before_reward"), new ScriptedRandomSource())).Reason);
        AssertNothingRan();
    }

    [Fact]
    public void Ended_match_refuses_abilities()
    {
        Player[] players = [Guest("Alice"), Guest("Bob")];
        var room = Started(players);
        room.Leave(players[1].Id, Now.AddMinutes(1));

        Assert.Equal(RoomStatus.Abandoned, room.Status);
        Assert.Equal(AbilityRefusal.NotPlaying, Use(room, players[0], """{"abilityId":"boost"}""", State("before_reward")).Reason);
        AssertNothingRan();
    }

    [Fact]
    public void Nft_traits_do_not_change_what_an_ability_does()
    {
        Player[] withNft = [Guest("Alice"), Guest("Bob")];
        Player[] withGuest = [Guest("Alice"), Guest("Bob")];
        var nftMatch = Started(withNft, NftChog(new NftTrait("Background", "Gold"), new NftTrait("Rarity", "Legendary")), ability: "steal");
        var guestMatch = Started(withGuest, ability: "steal");

        var nft = Use(nftMatch, withNft[0], """{"abilityId":"steal","target":2}""", State("own_turn"), new ScriptedRandomSource(1));
        var guest = Use(guestMatch, withGuest[0], """{"abilityId":"steal","target":2}""", State("own_turn"), new ScriptedRandomSource(1));

        Assert.True(nft.Used && guest.Used);
        Assert.Equal(guest.Ability, nft.Ability);
        Assert.Equal(guest.State!.Value.GetRawText(), nft.State!.Value.GetRawText());
    }

    [Fact]
    public void Only_the_locked_ability_can_be_used()
    {
        Player[] players = [Guest("Alice"), Guest("Bob")];
        var room = Started(players, ability: "steal");

        AssertRefusedIn(room, players[0], AbilityRefusal.NotYourAbility, """{"abilityId":"boost"}""");
        AssertRefusedIn(room, players[0], AbilityRefusal.UnknownAbility, """{"abilityId":"reward_boost"}""");
    }

    [Fact]
    public void Player_without_a_locked_ability_can_use_none()
    {
        Player[] players = [Guest("Alice"), Guest("Bob")];
        var room = Started(players, ability: "missing");

        Assert.Null(room.Members[0].Ability);
        AssertRefusedIn(room, players[0], AbilityRefusal.NotYourAbility, """{"abilityId":"boost"}""");
    }

    [Fact]
    public void Ability_is_used_at_most_its_max_uses()
    {
        Player[] players = [Guest("Alice"), Guest("Bob")];
        var room = Started(players);
        var first = Use(room, players[0], """{"abilityId":"boost"}""", State("before_reward"));
        Apply(room, players[0], first);

        Assert.Equal((1, 0), (room.Members[0].AbilityUses, room.Members[0].AbilityUsesLeft));
        AssertRefusedIn(room, players[0], AbilityRefusal.UsedUp, """{"abilityId":"boost"}""", expectedChecks: 1);
        Assert.Equal(1, _boost.Executions);
    }

    [Fact]
    public void Ability_with_several_uses_counts_each()
    {
        var catalog = AbilityCatalog.Create(GameType, 1, [Boost(), Steal(maxUses: 2)], [_boost, _steal], new StateWindow());
        var engine = new AbilityEngine([catalog]);
        Player[] players = [Guest("Alice"), Guest("Bob")];
        var room = Started(players, ability: "steal", catalog: catalog);

        for (var use = 1; use <= 2; use++)
        {
            var result = engine.Use(new AbilityRequest(room, players[0].Id, Payload("""{"abilityId":"steal","target":2}"""), State("own_turn"), new ScriptedRandomSource()));
            Assert.True(result.Used);
            Apply(room, players[0], result);
            Assert.Equal(2 - use, room.Members[0].AbilityUsesLeft);
        }

        var refused = engine.Use(new AbilityRequest(room, players[0].Id, Payload("""{"abilityId":"steal","target":2}"""), State("own_turn"), new ScriptedRandomSource()));
        Assert.Equal(AbilityRefusal.UsedUp, refused.Reason);
    }

    /// <summary>NFT-015: a deploy that changes the catalog mid-match doesn't change the values a locked ability uses.</summary>
    [Fact]
    public void Locked_values_apply_after_the_catalog_changes()
    {
        Player[] players = [Guest("Alice"), Guest("Bob")];
        var room = Started(players);
        var deployed = new AbilityEngine([AbilityCatalog.Create(GameType, 2, [Boost(percent: 50), Steal()], [_boost, _steal], new StateWindow())]);

        var result = deployed.Use(new AbilityRequest(room, players[0].Id, Payload("""{"abilityId":"boost"}"""), State("before_reward", reward: 100), new ScriptedRandomSource()));

        Assert.True(result.Used);
        Assert.Equal(25, result.Ability!.Parameters["percent"]);
        Assert.Equal(125, result.State!.Value.GetProperty("reward").GetInt32());
    }

    /// <summary>
    /// STORY-090: a value a deploy adds to a locked ability takes its handler's default, so the match keeps going as it
    /// did; a value without a default can't be applied.
    /// </summary>
    [Fact]
    public void A_value_added_after_the_lock_takes_its_default()
    {
        Player[] players = [Guest("Alice"), Guest("Bob")];
        var room = Started(players);
        var withDefault = new RecordingHandler("boost", new AbilityParameter("percent", 1, 100), new AbilityParameter("rounds", 1, 5, Default: 2));
        var withoutDefault = new RecordingHandler("boost", new AbilityParameter("percent", 1, 100), new AbilityParameter("rounds", 1, 5));
        AbilityDefinition Deployed() => AbilityDefinition.Create(
            "boost", "Boost", "Raise the pending reward.", AbilityTargetKind.None, ["before_reward"],
            new Dictionary<string, decimal> { ["percent"] = 50, ["rounds"] = 4 });
        var request = new AbilityRequest(room, players[0].Id, Payload("""{"abilityId":"boost"}"""), State("before_reward"), new ScriptedRandomSource());

        var used = new AbilityEngine([AbilityCatalog.Create(GameType, 2, [Deployed(), Steal()], [withDefault, _steal], new StateWindow())]).Use(request);
        var refused = new AbilityEngine([AbilityCatalog.Create(GameType, 2, [Deployed(), Steal()], [withoutDefault, _steal], new StateWindow())]).Use(request);

        Assert.True(used.Used);
        Assert.Equal(new Dictionary<string, decimal> { ["percent"] = 25, ["rounds"] = 2 }, withDefault.Seen);
        Assert.Equal(AbilityRefusal.Unavailable, refused.Reason);
        Assert.Null(withoutDefault.Seen);
    }

    [Fact]
    public void A_handler_default_outside_its_range_stops_the_catalog()
    {
        var handler = new RecordingHandler("boost", new AbilityParameter("percent", 1, 100, Default: 0));

        Assert.Throws<ArgumentException>(() => AbilityCatalog.Create(GameType, 1, [Boost()], [handler], new StateWindow()));
    }

    /// <summary>A handler that records the values it was given.</summary>
    private sealed class RecordingHandler(string abilityId, params AbilityParameter[] parameters) : IAbilityHandler
    {
        public Dictionary<string, decimal>? Seen { get; private set; }

        public string AbilityId => abilityId;

        public IReadOnlyList<AbilityParameter> Parameters => parameters;

        public string? CanExecute(AbilityContext context) => null;

        public JsonElement Execute(AbilityContext context)
        {
            Seen = context.Ability.Parameters.ToDictionary();
            return context.State!.Value;
        }
    }

    [Fact]
    public void Locked_ability_the_game_can_no_longer_apply_is_unavailable()
    {
        Player[] players = [Guest("Alice"), Guest("Bob")];
        var room = Started(players);
        var withoutBoost = new AbilityEngine([AbilityCatalog.Create(GameType, 2, [Steal()], [_steal], new StateWindow())]);
        var narrowerBoost = new AbilityEngine([AbilityCatalog.Create(GameType, 2, [Boost(percent: 10), Steal()], [new BoostHandler(maxPercent: 20), _steal], new StateWindow())]);
        var noCatalog = new AbilityEngine([]);
        var request = new AbilityRequest(room, players[0].Id, Payload("""{"abilityId":"boost"}"""), State("before_reward"), new ScriptedRandomSource());

        Assert.Equal(AbilityRefusal.Unavailable, withoutBoost.Use(request).Reason);
        Assert.Equal(AbilityRefusal.Unavailable, narrowerBoost.Use(request).Reason);
        Assert.Equal(AbilityRefusal.Unavailable, noCatalog.Use(request).Reason);
        AssertNothingRan();
    }

    /// <summary>
    /// The contract games implement sees players as ids and seats only (NFT-016, ADR-007): nothing a handler or the
    /// game's timing receives can reach a character, NFT traits or the room's members.
    /// </summary>
    [Fact]
    public void Ability_contract_cannot_reach_characters_or_nft_traits()
    {
        Type[] contract = [typeof(IAbilityHandler), typeof(IAbilityTiming), typeof(AbilityContext), typeof(AbilityPlayer), typeof(AbilityDefinition), typeof(AbilityParameter), typeof(AbilityCatalog)];
        var forbidden = new[] { typeof(NftTrait).Namespace!, typeof(CharacterSnapshot).Namespace!, typeof(GameRoom).Namespace!, typeof(WalletAddress).Namespace! };

        var reachable = contract.SelectMany(type => ReachableTypes(type)).Distinct().ToArray();

        Assert.DoesNotContain(reachable, type => forbidden.Contains(type.Namespace));
    }

    private static IEnumerable<Type> ReachableTypes(Type type)
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        var signatures = type.GetProperties(all).Select(p => p.PropertyType)
            .Concat(type.GetMethods(all).SelectMany(m => m.GetParameters().Select(p => p.ParameterType).Append(m.ReturnType)))
            .Concat(type.GetConstructors().SelectMany(c => c.GetParameters().Select(p => p.ParameterType)));
        return signatures.SelectMany(Expand);

        static IEnumerable<Type> Expand(Type t) =>
            (t.HasElementType ? Expand(t.GetElementType()!) : [t]).Concat(t.IsGenericType ? t.GetGenericArguments().SelectMany(Expand) : []);
    }

    [Fact]
    public void Malformed_reason_from_a_game_is_a_bug_not_a_refusal()
    {
        var engine = new AbilityEngine([AbilityCatalog.Create(GameType, 1, [Boost()], [new SloppyBoost()], new StateWindow())]);
        Player[] players = [Guest("Alice"), Guest("Bob")];
        var room = Started(players);

        Assert.Throws<InvalidOperationException>(() =>
            engine.Use(new AbilityRequest(room, players[0].Id, Payload("""{"abilityId":"boost"}"""), State("before_reward"), new ScriptedRandomSource())));
    }

    private sealed class SloppyBoost : IAbilityHandler
    {
        public string AbilityId => "boost";

        public IReadOnlyList<AbilityParameter> Parameters { get; } = [new("percent", 1, 100)];

        public string? CanExecute(AbilityContext context) => "Not now!";

        public JsonElement Execute(AbilityContext context) => throw new InvalidOperationException("Never reached.");
    }

    /// <summary>Seat 1 uses the ability in a three-player match; the refusal changes nothing and reaches no handler.</summary>
    private void AssertRefused(string reason, string payload, JsonElement? state, bool leaveSeat3 = false)
    {
        Player[] players = [Guest("Alice"), Guest("Bob"), Guest("Carol")];
        var room = Started(players, ability: NamedIn(payload));
        if (leaveSeat3)
        {
            room.Leave(players[2].Id, Now.AddMinutes(1));
        }

        var version = room.Version;
        var random = new ScriptedRandomSource();

        var result = Use(room, players[0], payload, state, random);

        Assert.Equal((false, reason, null, null), (result.Used, result.Reason, result.Ability, result.State));
        Assert.Empty(random.Draws);
        Assert.Equal(version, room.Version);
        AssertNothingRan();
    }

    private void AssertRefusedIn(GameRoom room, Player player, string reason, string payload, int expectedChecks = 0)
    {
        var version = room.Version;
        var uses = room.Members.Select(m => m.AbilityUses).ToArray();
        var random = new ScriptedRandomSource();

        var result = Use(room, player, payload, State("before_reward"), random);

        Assert.Equal((false, reason, null), (result.Used, result.Reason, result.State));
        Assert.Empty(random.Draws);
        Assert.Equal(version, room.Version);
        Assert.Equal(uses, room.Members.Select(m => m.AbilityUses));
        Assert.Equal(expectedChecks, _boost.Checks + _steal.Checks);
    }

    private void AssertNothingRan() => Assert.Equal((0, 0, 0), (_boost.Checks, _steal.Checks, _boost.Executions));
}
