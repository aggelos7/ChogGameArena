using System.Text.Json;
using ChogGames.Core.Abilities;
using ChogGames.Core.Games;
using ChogGames.Core.Matches;
using ChogGames.Core.Players;
using ChogGames.Core.Rooms;
using ChogGames.Games.ChogLife.Abilities;
using ChogGames.Games.ChogLife.Rules;

namespace ChogGames.Games.ChogLife.Tests;

/// <summary>
/// STORY-042, DEC-044: Chog Life's four abilities through the shared engine. Reward Boost, Loss Protection and Lucky
/// Reroll are armed before a roll and act on the first tile outcome they fit. Event Preview is armed at a fork; choosing
/// a lane then draws the next move there and shows only its player the die, tile and event outcome. Keeping the lane
/// plays it out exactly as shown; switching moves the stake and draws the next roll afresh. STORY-090, DEC-077: with a
/// holder Tier's locked values, the boost and the protection are stronger, Lucky Reroll draws more often and Event
/// Preview has more uses. STORY-091, DEC-078: Double Dice rolls its dice at once and the roll moves by the one kept (the
/// highest when the turn runs out); Tax the Rich moves a share of the richest opponent's liquid MON, capped.
/// </summary>
public class ChogLifeAbilityTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    private static readonly ChogLifeMatchRules Rules = new([LifeRulesV1.Rules]);

    private static readonly AbilityCatalog Catalog = LifeAbilities.Catalog(Rules);

    private static readonly AbilityEngine Engine = new([Catalog]);

    private static readonly JsonElement NoPayload = JsonSerializer.SerializeToElement(new { });

    /// <summary>Hands out the queued values and records every draw; drawing more than queued fails the test.</summary>
    private sealed class Dice(params int[] values) : IRandomSource
    {
        private readonly Queue<int> _values = new(values);

        public List<(int Min, int Max)> Draws { get; } = [];

        public int Next(int minInclusive, int maxExclusive)
        {
            Draws.Add((minInclusive, maxExclusive));
            return _values.Dequeue();
        }
    }

    private sealed class KeepOrder : IRandomSource
    {
        public int Next(int minInclusive, int maxExclusive) => maxExclusive - 1;
    }

    /// <summary>A started two-player match: seat 1 locked <paramref name="ability"/>, seat 2 Reward Boost.</summary>
    private static (GameRoom Room, Player[] Players) Started(string ability) =>
        StartedWith(Catalog.Definitions.Single(d => d.Id == ability));

    /// <summary>The catalog's ability with a holder Tier's values and uses (STORY-090, DEC-077).</summary>
    private static AbilityDefinition Tiered(string id, Dictionary<string, decimal> values, int? uses = null)
    {
        var defined = Catalog.Definitions.Single(d => d.Id == id);
        return defined.WithValues(values, uses ?? defined.MaxUses);
    }

    /// <summary>
    /// A started match of <paramref name="count"/> players: seat 1 locked <paramref name="definition"/> with its values, the
    /// others Reward Boost.
    /// </summary>
    private static (GameRoom Room, Player[] Players) StartedWith(AbilityDefinition definition, int count = 2)
    {
        var ability = definition.Id;
        var players = Enumerable.Range(1, count).Select(n =>
        {
            Assert.True(PlayerDisplayName.TryCreate($"Player {n}", out var name));
            return Player.CreateGuest(name, Now);
        }).ToArray();
        Assert.True(RoomCode.TryParse("K7QF3M", out var code));
        var room = GameRoom.Create(GameDefinition.Create(GameDefinition.ChogLife, 2, 6), players[0], code, Now);
        foreach (var (player, n) in players.Skip(1).Select((player, n) => (player, n)))
        {
            room.Join(player, Now.AddSeconds(1 + n));
            room.SelectAbility(player.Id, LifeAbilities.RewardBoost);
        }

        room.SelectAbility(players[0].Id, ability);
        foreach (var player in players)
        {
            room.MarkReady(player);
        }

        var random = new KeepOrder();
        room.Start(
            players[0].Id,
            players,
            players.ToDictionary(p => p.Id, _ => CharacterSnapshot.Standard(DefaultAvatar.Classic, Now)),
            TimeSpan.FromSeconds(20),
            random,
            Now.AddMinutes(1),
            players.ToDictionary(p => p.Id, p => p == players[0] ? AbilitySnapshot.Lock(Catalog, definition) : Locked(LifeAbilities.RewardBoost)),
            started => Rules.StartState(new MatchStartContext(started, random))?.GetRawText());
        return (room, players);
    }

    private static AbilitySnapshot Locked(string id) =>
        AbilitySnapshot.Lock(Catalog, Catalog.Definitions.Single(d => d.Id == id));

    private static AbilityResult Use(GameRoom room, Player player, string ability, string state, IRandomSource? random = null)
    {
        using var document = JsonDocument.Parse(state);
        return Engine.Use(new AbilityRequest(
            room,
            player.Id,
            JsonSerializer.SerializeToElement(new { abilityId = ability }),
            document.RootElement.Clone(),
            random ?? new Dice()));
    }

    /// <summary>Uses the ability and returns the new state; the use must be accepted.</summary>
    private static string Armed(GameRoom room, Player player, string ability, string state, IRandomSource? random = null)
    {
        var result = Use(room, player, ability, state, random);
        Assert.True(result.Used, result.Reason);
        return result.State!.Value.GetRawText();
    }

    private static MatchRuleDecision Apply(GameRoom room, Player player, string type, string state, IRandomSource dice, object? payload = null)
    {
        using var document = JsonDocument.Parse(state);
        return Rules.Apply(new MatchCommandContext(
            room,
            player.Id,
            type,
            payload is null ? NoPayload : JsonSerializer.SerializeToElement(payload),
            document.RootElement.Clone(),
            dice));
    }

    private static string Roll(GameRoom room, Player player, string state, IRandomSource dice)
    {
        var decision = Apply(room, player, ChogLifeMatchRules.Roll, state, dice);
        Assert.Equal(MatchRuleOutcome.Accepted, decision.Outcome);
        return decision.State!.Value.GetRawText();
    }

    private static JsonElement View(GameRoom room, Player viewer, string state)
    {
        using var document = JsonDocument.Parse(state);
        return Rules.ViewFor(new MatchViewContext(room, viewer.Id, document.RootElement))!.Value.Clone();
    }

    private static JsonElement LastMove(string state) => JsonDocument.Parse(state).RootElement.GetProperty("lastMove").Clone();

    private static JsonElement PawnView(GameRoom room, Player viewer, string state, int seat) =>
        View(room, viewer, state).GetProperty("pawns").EnumerateArray().Single(p => p.GetProperty("seat").GetInt32() == seat);

    private static string State(string pawn1, string pawn2 = """{"seat":2,"tile":0,"mon":100}""", string extra = "") =>
        $$"""{"rulesVersion":1,"pawns":[{{pawn1}},{{pawn2}}]{{extra}}}""";

    /// <summary>Seat 1 rolled from tile 6 onto the first fork (tile 9) and is choosing a lane.</summary>
    private static string AtFork(GameRoom room, Player player) =>
        Apply(room, player, ChogLifeMatchRules.Roll, State("""{"seat":1,"tile":6,"mon":100}"""), new Dice(3)).State!.Value.GetRawText();

    [Fact]
    public void The_catalog_offers_the_six_abilities_once_each_with_their_windows_and_values()
    {
        Assert.Equal((GameDefinition.ChogLife, 3), (Catalog.GameType, Catalog.Version));
        Assert.Equal(
            [
                ("reward_boost", "Reward Boost", "before_roll", "percent=25"),
                ("loss_protection", "Loss Protection", "before_roll", "percent=50"),
                ("event_preview", "Event Preview", "at_fork", ""),
                ("event_reroll", "Lucky Reroll", "before_roll", "draws=2"),
                ("double_dice", "Double Dice", "before_roll", "dice=2"),
                ("tax_the_rich", "Tax the Rich", "before_roll", "cap=50,percent=10"),
            ],
            Catalog.Definitions.Select(d => (
                d.Id,
                d.Name,
                string.Join(",", d.Windows),
                string.Join(",", d.Parameters.Select(p => $"{p.Key}={p.Value}")))));
        Assert.All(Catalog.Definitions, d => Assert.Equal((1, AbilityTargetKind.None), (d.MaxUses, d.Target)));
    }

    [Fact]
    public void Reward_boost_adds_a_quarter_of_the_next_reward_to_bonuses_and_is_spent()
    {
        var (room, players) = Started(LifeAbilities.RewardBoost);
        var armed = Armed(room, players[0], LifeAbilities.RewardBoost, room.State!);
        Assert.Equal("reward_boost", PawnView(room, players[1], armed, 1).GetProperty("armed").GetString());

        var rolled = Roll(room, players[0], armed, new Dice(2));

        Assert.Equal("""{"seat":1,"roll":2,"from":0,"to":2,"path":[1,2],"change":20,"boost":5}""", LastMove(rolled).GetRawText());
        var pawn = PawnView(room, players[0], rolled, 1);
        Assert.Equal((120, 5, 125), (pawn.GetProperty("mon").GetInt32(), pawn.GetProperty("bonuses").GetInt32(), pawn.GetProperty("portfolio").GetInt32()));
        Assert.False(pawn.TryGetProperty("armed", out _));
    }

    [Fact]
    public void Reward_boost_raises_an_event_gain_rounding_down()
    {
        var (room, players) = Started(LifeAbilities.RewardBoost);
        var armed = Armed(room, players[0], LifeAbilities.RewardBoost, room.State!);

        // Tile 3 is an airdrop; 0 draws its first outcome, +10 MON.
        var rolled = Roll(room, players[0], armed, new Dice(3, 0));

        Assert.Equal((10, 2), (LastMove(rolled).GetProperty("change").GetInt32(), LastMove(rolled).GetProperty("boost").GetInt32()));
    }

    [Theory]
    [InlineData(1)] // a normal tile
    [InlineData(5)] // a loss
    public void Reward_boost_stays_armed_through_outcomes_it_does_not_fit(int roll)
    {
        var (room, players) = Started(LifeAbilities.RewardBoost);
        var armed = Armed(room, players[0], LifeAbilities.RewardBoost, room.State!);

        var rolled = Roll(room, players[0], armed, new Dice(roll));

        Assert.False(LastMove(rolled).TryGetProperty("boost", out _));
        Assert.Equal("reward_boost", PawnView(room, players[0], rolled, 1).GetProperty("armed").GetString());
    }

    [Fact]
    public void A_stake_payout_is_never_boosted()
    {
        var (room, players) = Started(LifeAbilities.RewardBoost);
        var onSafeLane = State("""{"seat":1,"tile":12,"mon":50,"stake":{"lane":"Safe","amount":50},"armed":{"ability":"reward_boost","percent":25}}""");

        // 13, then 18 on the main track: the stake pays 62 back, and 18 is a normal tile.
        var rolled = Roll(room, players[0], onSafeLane, new Dice(2));

        Assert.False(LastMove(rolled).TryGetProperty("boost", out _));
        Assert.Equal(62, LastMove(rolled).GetProperty("matured").GetProperty("payout").GetInt32());
        Assert.Equal("reward_boost", PawnView(room, players[0], rolled, 1).GetProperty("armed").GetString());
    }

    [Fact]
    public void Loss_protection_halves_the_next_loss_tile_and_is_spent()
    {
        var (room, players) = Started(LifeAbilities.LossProtection);
        var armed = Armed(room, players[0], LifeAbilities.LossProtection, room.State!);

        var rolled = Roll(room, players[0], armed, new Dice(5));

        Assert.Equal("""{"seat":1,"roll":5,"from":0,"to":5,"path":[1,2,3,4,5],"change":-8,"saved":7}""", LastMove(rolled).GetRawText());
        Assert.Equal(92, PawnView(room, players[0], rolled, 1).GetProperty("mon").GetInt32());
        Assert.False(PawnView(room, players[0], rolled, 1).TryGetProperty("armed", out _));
    }

    [Fact]
    public void Loss_protection_halves_an_event_loss_before_it_becomes_debt()
    {
        var (room, players) = Started(LifeAbilities.LossProtection);
        var armed = Armed(room, players[0], LifeAbilities.LossProtection, State("""{"seat":1,"tile":12,"mon":0}"""));

        // 12 → 13 → 18 → 19, a market: 99 draws the 10% loss, at least 5 MON, which halves to 3 of debt.
        var rolled = Roll(room, players[0], armed, new Dice(3, 99));

        Assert.Equal((-3, 2), (LastMove(rolled).GetProperty("change").GetInt32(), LastMove(rolled).GetProperty("saved").GetInt32()));
        Assert.Equal(3, PawnView(room, players[0], rolled, 1).GetProperty("liabilities").GetInt32());
    }

    [Fact]
    public void Loss_protection_stays_armed_through_a_gain()
    {
        var (room, players) = Started(LifeAbilities.LossProtection);
        var armed = Armed(room, players[0], LifeAbilities.LossProtection, room.State!);

        var rolled = Roll(room, players[0], armed, new Dice(2));

        Assert.Equal(20, LastMove(rolled).GetProperty("change").GetInt32());
        Assert.Equal("loss_protection", PawnView(room, players[0], rolled, 1).GetProperty("armed").GetString());
    }

    [Theory]
    [InlineData(99, 0, 10, -10)] // a loss, then a gain: the gain applies
    [InlineData(0, 99, 10, -10)] // a gain, then a loss: the gain still applies
    [InlineData(99, 99, -10, -10)] // two losses: one applies
    public void Lucky_reroll_draws_an_event_twice_and_keeps_the_better(int first, int second, int change, int discarded)
    {
        var (room, players) = Started(LifeAbilities.EventReroll);
        var armed = Armed(room, players[0], LifeAbilities.EventReroll, room.State!);
        var dice = new Dice(6, first, second);

        // Tile 6 is a market: under 50 gains 10%, from 50 loses 10%.
        var rolled = Roll(room, players[0], armed, dice);

        Assert.Equal([(1, 7), (0, 100), (0, 100)], dice.Draws);
        Assert.Equal((change, discarded), (LastMove(rolled).GetProperty("change").GetInt32(), LastMove(rolled).GetProperty("rerolled").GetInt32()));
        Assert.False(PawnView(room, players[0], rolled, 1).TryGetProperty("armed", out _));
    }

    [Fact]
    public void A_legendary_reward_boost_adds_half_of_the_next_reward()
    {
        var (room, players) = StartedWith(Tiered(LifeAbilities.RewardBoost, new() { [LifeAbilities.Percent] = 50 }));
        var armed = Armed(room, players[0], LifeAbilities.RewardBoost, room.State!);

        var rolled = Roll(room, players[0], armed, new Dice(2));

        Assert.Equal((20, 10), (LastMove(rolled).GetProperty("change").GetInt32(), LastMove(rolled).GetProperty("boost").GetInt32()));
    }

    [Fact]
    public void A_legendary_loss_protection_takes_only_a_quarter_of_the_next_loss()
    {
        var (room, players) = StartedWith(Tiered(LifeAbilities.LossProtection, new() { [LifeAbilities.Percent] = 25 }));
        var armed = Armed(room, players[0], LifeAbilities.LossProtection, room.State!);

        // The loss tile at 5 takes 15: 25% of it is taken, rounding up, and 11 saved.
        var rolled = Roll(room, players[0], armed, new Dice(5));

        Assert.Equal("""{"seat":1,"roll":5,"from":0,"to":5,"path":[1,2,3,4,5],"change":-4,"saved":11}""", LastMove(rolled).GetRawText());
        Assert.Equal(96, PawnView(room, players[0], rolled, 1).GetProperty("mon").GetInt32());
    }

    [Theory]
    [InlineData(99, 99, 0, 10)] // two losses, then a gain: the gain applies
    [InlineData(99, 99, 99, -10)] // three losses: one applies
    public void A_rare_lucky_reroll_draws_an_event_three_times_and_keeps_the_best(int first, int second, int third, int change)
    {
        var (room, players) = StartedWith(Tiered(LifeAbilities.EventReroll, new() { [LifeAbilities.Draws] = 3 }));
        var armed = Armed(room, players[0], LifeAbilities.EventReroll, room.State!);
        var dice = new Dice(6, first, second, third);

        var rolled = Roll(room, players[0], armed, dice);

        Assert.Equal([(1, 7), (0, 100), (0, 100), (0, 100)], dice.Draws);
        var move = LastMove(rolled);
        Assert.Equal((change, -10, 3), (move.GetProperty("change").GetInt32(), move.GetProperty("rerolled").GetInt32(), move.GetProperty("draws").GetInt32()));
    }

    [Fact]
    public void A_lucky_reroll_armed_before_tier_values_draws_twice()
    {
        var (room, players) = Started(LifeAbilities.EventReroll);
        var armed = State("""{"seat":1,"tile":0,"mon":100,"armed":{"ability":"event_reroll"}}""");
        var dice = new Dice(6, 99, 0);

        var rolled = Roll(room, players[0], armed, dice);

        Assert.Equal([(1, 7), (0, 100), (0, 100)], dice.Draws);
        Assert.Equal((10, 2), (LastMove(rolled).GetProperty("change").GetInt32(), LastMove(rolled).GetProperty("draws").GetInt32()));
    }

    [Fact]
    public void An_epic_event_preview_has_two_uses()
    {
        var (room, players) = StartedWith(Tiered(LifeAbilities.EventPreview, [], uses: 2));
        var member = room.Members.Single(m => m.PlayerId == players[0].Id);

        Assert.Equal((2, 2), (member.Ability!.MaxUses, member.AbilityUsesLeft));
        Assert.EndsWith("2 uses per game.", member.Ability.Description);
        Assert.EndsWith("1 use per game.", Catalog.Definitions.Single(d => d.Id == LifeAbilities.EventPreview).Description);
    }

    [Theory]
    [InlineData(2)] // a reward tile
    [InlineData(5)] // a loss tile
    public void Lucky_reroll_stays_armed_off_event_tiles(int roll)
    {
        var (room, players) = Started(LifeAbilities.EventReroll);
        var armed = Armed(room, players[0], LifeAbilities.EventReroll, room.State!);

        var rolled = Roll(room, players[0], armed, new Dice(roll));

        Assert.False(LastMove(rolled).TryGetProperty("rerolled", out _));
        Assert.Equal("event_reroll", PawnView(room, players[0], rolled, 1).GetProperty("armed").GetString());
    }

    [Fact]
    public void Lucky_reroll_stays_armed_on_an_event_with_one_outcome()
    {
        var (room, players) = Started(LifeAbilities.EventReroll);
        var armed = Armed(room, players[0], LifeAbilities.EventReroll, State("""{"seat":1,"tile":9,"mon":100,"lane":"Degen"}"""));
        var dice = new Dice(1);

        // Tile 14 is a rug, which always takes 40%.
        var rolled = Roll(room, players[0], armed, dice);

        Assert.Equal([(1, 7)], dice.Draws);
        Assert.Equal(-40, LastMove(rolled).GetProperty("change").GetInt32());
        Assert.Equal("event_reroll", PawnView(room, players[0], rolled, 1).GetProperty("armed").GetString());
    }

    [Fact]
    public void Arming_draws_nothing_and_changes_only_the_users_pawn()
    {
        var (room, players) = Started(LifeAbilities.RewardBoost);
        var dice = new Dice();

        var armed = Armed(room, players[0], LifeAbilities.RewardBoost, room.State!, dice);

        Assert.Empty(dice.Draws);
        Assert.Equal(
            State("""{"seat":1,"tile":0,"mon":100,"armed":{"ability":"reward_boost","percent":25}}"""),
            armed);
    }

    [Fact]
    public void An_ability_already_armed_cannot_be_armed_again()
    {
        var (room, players) = Started(LifeAbilities.LossProtection);
        var armed = Armed(room, players[0], LifeAbilities.LossProtection, room.State!);

        Assert.Equal("ability_armed", Use(room, players[0], LifeAbilities.LossProtection, armed).Reason);
    }

    [Theory]
    [InlineData(LifeAbilities.RewardBoost)]
    [InlineData(LifeAbilities.LossProtection)]
    [InlineData(LifeAbilities.EventReroll)]
    public void Armed_abilities_cannot_be_used_while_choosing_a_lane(string ability)
    {
        var (room, players) = Started(ability);

        Assert.Equal(AbilityRefusal.NotNow, Use(room, players[0], ability, AtFork(room, players[0])).Reason);
    }

    [Fact]
    public void Event_preview_can_only_be_used_while_choosing_a_lane()
    {
        var (room, players) = Started(LifeAbilities.EventPreview);
        var chosen = Apply(room, players[0], ChogLifeMatchRules.ChooseLane, AtFork(room, players[0]), new Dice(), new { lane = "Safe" });

        Assert.Equal(AbilityRefusal.NotNow, Use(room, players[0], LifeAbilities.EventPreview, room.State!).Reason);
        Assert.Equal(AbilityRefusal.NotNow, Use(room, players[0], LifeAbilities.EventPreview, chosen.State!.Value.GetRawText()).Reason);
    }

    [Theory]
    [InlineData("""{"rulesVersion":1,"pawns":[{"seat":1,"tile":35,"mon":100},{"seat":2,"tile":3,"mon":100}],"finished":[1]}""")]
    [InlineData("""{"rulesVersion":1,"pawns":[{"seat":1,"tile":3,"mon":100},{"seat":2,"tile":3,"mon":100}],"ended":"TimeUp"}""")]
    public void No_ability_can_be_used_once_the_player_has_finished_or_the_match_has_ended(string state)
    {
        var (room, players) = Started(LifeAbilities.RewardBoost);

        Assert.Equal(AbilityRefusal.NotNow, Use(room, players[0], LifeAbilities.RewardBoost, state).Reason);
    }

    [Theory]
    [InlineData("""{"rulesVersion":9,"pawns":[]}""")]
    [InlineData("""{"rulesVersion":1,"pawns":[{"seat":1,"tile":3,"mon":-1},{"seat":2,"tile":0,"mon":100}]}""")]
    [InlineData("""{"rulesVersion":1,"pawns":[{"seat":2,"tile":0,"mon":100}]}""")]
    public void No_ability_can_be_used_on_a_state_the_server_cannot_play(string state)
    {
        var (room, players) = Started(LifeAbilities.RewardBoost);

        Assert.Equal(AbilityRefusal.NotNow, Use(room, players[0], LifeAbilities.RewardBoost, state).Reason);
    }

    [Theory]
    [InlineData("""{"ability":"reward_boost"}""")]
    [InlineData("""{"ability":"reward_boost","percent":0}""")]
    [InlineData("""{"ability":"event_reroll","percent":25}""")]
    [InlineData("""{"ability":"event_preview"}""")]
    [InlineData("""{"ability":"steal"}""")]
    public void A_roll_with_an_unknown_armed_ability_changes_nothing(string armed)
    {
        var (room, players) = Started(LifeAbilities.RewardBoost);
        var dice = new Dice(2);

        var decision = Apply(room, players[0], ChogLifeMatchRules.Roll, State($$"""{"seat":1,"tile":0,"mon":100,"armed":{{armed}}}"""), dice);

        Assert.Equal(("game_state_unavailable", 0), (decision.Reason, dice.Draws.Count));
    }

    private static MatchRuleDecision Choose(GameRoom room, Player player, string state, string lane, IRandomSource? dice = null) =>
        Apply(room, player, ChogLifeMatchRules.ChooseLane, state, dice ?? new Dice(), new { lane });

    /// <summary>Seat 1 at the first fork with Event Preview armed, then choosing Degen: die 6, 10 for the stake, 50 for the event.</summary>
    private static (GameRoom Room, Player[] Players, string State) PreviewedDegen()
    {
        var (room, players) = Started(LifeAbilities.EventPreview);
        var armed = Armed(room, players[0], LifeAbilities.EventPreview, AtFork(room, players[0]));
        return (room, players, Choose(room, players[0], armed, "Degen", new Dice(6, 10, 50)).State!.Value.GetRawText());
    }

    // Degen from tile 9 with a 6: 14 to 17, then 18 and 19 on the main track, a market; 50 draws its 10% loss.
    private const string DegenPreview =
        """{"lane":"Degen","roll":6,"to":19,"path":[14,15,16,17,18,19],"outcome":{"chance":50,"effect":"Loss","amount":0,"percent":10,"minimum":5}}""";

    [Fact]
    public void Using_event_preview_draws_nothing_and_keeps_the_lane_choice_open()
    {
        var (room, players) = Started(LifeAbilities.EventPreview);
        var dice = new Dice();

        var armed = Armed(room, players[0], LifeAbilities.EventPreview, AtFork(room, players[0]), dice);

        Assert.Empty(dice.Draws);
        var view = View(room, players[0], armed);
        Assert.Equal(1, view.GetProperty("choosing").GetInt32());
        Assert.Equal("event_preview", PawnView(room, players[1], armed, 1).GetProperty("armed").GetString());
        Assert.False(view.TryGetProperty("preview", out _));
    }

    [Fact]
    public void Choosing_a_lane_with_event_preview_shows_only_its_player_the_next_move_there_and_keeps_the_turn()
    {
        var (room, players) = Started(LifeAbilities.EventPreview);
        var armed = Armed(room, players[0], LifeAbilities.EventPreview, AtFork(room, players[0]));
        var dice = new Dice(6, 10, 50);

        var decision = Choose(room, players[0], armed, "Degen", dice);

        Assert.Equal((MatchRuleOutcome.Accepted, false), (decision.Outcome, decision.EndsTurn));
        Assert.Equal([(1, 7), (0, 100), (0, 100)], dice.Draws);
        var previewed = decision.State!.Value.GetRawText();
        var own = View(room, players[0], previewed);
        Assert.Equal(DegenPreview, own.GetProperty("preview").GetRawText());
        Assert.Equal(1, own.GetProperty("confirming").GetInt32());
        Assert.False(own.TryGetProperty("choosing", out _));
        Assert.False(PawnView(room, players[0], previewed, 1).TryGetProperty("armed", out _));

        var other = View(room, players[1], previewed);
        Assert.False(other.TryGetProperty("preview", out _));
        Assert.DoesNotContain("preview", other.GetRawText(), StringComparison.Ordinal);
        Assert.Equal(1, other.GetProperty("confirming").GetInt32());
    }

    [Fact]
    public void A_preview_never_shows_the_stake_payout()
    {
        var (room, players, previewed) = PreviewedDegen();

        var preview = View(room, players[0], previewed).GetProperty("preview").GetRawText();

        Assert.DoesNotContain("payout", preview, StringComparison.Ordinal);
        Assert.DoesNotContain("matured", preview, StringComparison.Ordinal);
    }

    [Fact]
    public void Keeping_the_lane_ends_the_turn_and_the_roll_plays_out_exactly_as_shown()
    {
        var (room, players, previewed) = PreviewedDegen();

        var kept = Choose(room, players[0], previewed, "Degen");
        Assert.Equal((MatchRuleOutcome.Accepted, true), (kept.Outcome, kept.EndsTurn));
        var state = kept.State!.Value.GetRawText();
        Assert.Equal(DegenPreview, View(room, players[0], state).GetProperty("preview").GetRawText());
        Assert.False(View(room, players[0], state).TryGetProperty("confirming", out _));
        var noDraws = new Dice();
        var rolled = Roll(room, players[0], state, noDraws);

        // 10 tripled the 50 MON stake to 150, so the market's 10% loss of 200 liquid MON is 20.
        Assert.Empty(noDraws.Draws);
        Assert.Equal(
            """{"seat":1,"roll":6,"from":9,"to":19,"path":[14,15,16,17,18,19],"change":-20,"matured":{"lane":"Degen","stake":50,"payout":150}}""",
            LastMove(rolled).GetRawText());
        Assert.False(View(room, players[0], rolled).TryGetProperty("preview", out _));
        Assert.DoesNotContain("preview", rolled, StringComparison.Ordinal);
    }

    [Fact]
    public void Switching_lanes_moves_the_stake_throws_the_preview_away_and_the_roll_is_drawn_afresh()
    {
        var (room, players, previewed) = PreviewedDegen();

        var switched = Choose(room, players[0], previewed, "Safe");

        Assert.Equal((MatchRuleOutcome.Accepted, true), (switched.Outcome, switched.EndsTurn));
        var state = switched.State!.Value.GetRawText();
        Assert.Equal(
            """{"seat":1,"roll":3,"from":6,"to":9,"path":[7,8,9],"lane":"Safe","staked":50,"switched":true}""",
            LastMove(state).GetRawText());
        var pawn = PawnView(room, players[0], state, 1);
        Assert.Equal(("Safe", "Safe", 50), (pawn.GetProperty("lane").GetString(), pawn.GetProperty("stake").GetProperty("lane").GetString(), pawn.GetProperty("stake").GetProperty("amount").GetInt32()));
        Assert.False(View(room, players[0], state).TryGetProperty("preview", out _));
        Assert.DoesNotContain("preview", state, StringComparison.Ordinal);

        var dice = new Dice(1);
        var rolled = Roll(room, players[0], state, dice);
        Assert.Equal([(1, 7)], dice.Draws);
        Assert.Equal("""{"seat":1,"roll":1,"from":9,"to":10,"path":[10],"change":10}""", LastMove(rolled).GetRawText());
    }

    [Fact]
    public void While_keeping_or_switching_nothing_else_can_be_done()
    {
        var (room, players, previewed) = PreviewedDegen();
        var dice = new Dice();

        Assert.Equal("choose_lane_first", Apply(room, players[0], ChogLifeMatchRules.Roll, previewed, dice).Reason);
        Assert.Equal("invalid_lane", Choose(room, players[0], previewed, "Main").Reason);
        Assert.Equal(AbilityRefusal.NotNow, Use(room, players[0], LifeAbilities.EventPreview, previewed, dice).Reason);
        Assert.Empty(dice.Draws);
    }

    [Fact]
    public void A_turn_that_runs_out_while_deciding_keeps_the_previewed_lane()
    {
        var (room, players, previewed) = PreviewedDegen();
        var shown = View(room, players[0], previewed).GetProperty("preview").GetRawText();

        using var document = JsonDocument.Parse(previewed);
        var timedOut = Rules.TimeOutTurn(new MatchTurnContext(room, document.RootElement, new Dice()))!.Value.GetRawText();

        Assert.False(View(room, players[0], timedOut).TryGetProperty("confirming", out _));
        Assert.Equal(shown, View(room, players[0], timedOut).GetProperty("preview").GetRawText());
        Assert.False(LastMove(timedOut).TryGetProperty("switched", out _));
        Assert.Equal(19, LastMove(Roll(room, players[0], timedOut, new Dice())).GetProperty("to").GetInt32());
    }

    [Fact]
    public void A_turn_that_runs_out_before_choosing_takes_the_default_lane_and_still_previews_it()
    {
        var (room, players) = Started(LifeAbilities.EventPreview);
        var armed = Armed(room, players[0], LifeAbilities.EventPreview, AtFork(room, players[0]));
        var dice = new Dice(1, 0, 0);

        using var document = JsonDocument.Parse(armed);
        var timedOut = Rules.TimeOutTurn(new MatchTurnContext(room, document.RootElement, dice))!.Value.GetRawText();

        Assert.Equal(3, dice.Draws.Count);
        var view = View(room, players[0], timedOut);
        Assert.False(view.TryGetProperty("confirming", out _));
        Assert.Equal("""{"lane":"Safe","roll":1,"to":10,"path":[10]}""", view.GetProperty("preview").GetRawText());
        Assert.True(LastMove(timedOut).GetProperty("byDefault").GetBoolean());
    }

    [Fact]
    public void Event_preview_already_armed_cannot_be_used_again()
    {
        var (room, players) = Started(LifeAbilities.EventPreview);
        var armed = Armed(room, players[0], LifeAbilities.EventPreview, AtFork(room, players[0]));

        Assert.Equal("ability_armed", Use(room, players[0], LifeAbilities.EventPreview, armed).Reason);
    }

    [Theory]
    [InlineData("[0,10,50]")]
    [InlineData("[7,10,50]")]
    [InlineData("[6,100,50]")]
    [InlineData("[6,10]")]
    public void A_roll_with_preview_draws_out_of_range_changes_nothing(string drawn)
    {
        var (room, players) = Started(LifeAbilities.EventPreview);
        var dice = new Dice(2);

        var decision = Apply(room, players[0], ChogLifeMatchRules.Roll, State($$"""{"seat":1,"tile":9,"mon":100,"lane":"Safe","preview":{{drawn}}}"""), dice);

        Assert.Equal(("game_state_unavailable", 0), (decision.Reason, dice.Draws.Count));
    }

    [Theory]
    [InlineData("""{"seat":1,"tile":3,"mon":100,"preview":[6,10,50]}""")] // off a fork
    [InlineData("""{"seat":1,"tile":9,"mon":100,"preview":[6,10,50]}""")] // no lane chosen
    [InlineData("""{"seat":1,"tile":9,"mon":100,"lane":"Safe","armed":{"ability":"event_preview"}}""")] // armed after choosing
    public void Previews_are_only_valid_on_a_fork_after_its_lane_is_chosen(string pawn)
    {
        var (room, players) = Started(LifeAbilities.EventPreview);
        var dice = new Dice(2);

        var decision = Apply(room, players[0], ChogLifeMatchRules.Roll, State(pawn), dice);

        Assert.Equal(("game_state_unavailable", 0), (decision.Reason, dice.Draws.Count));
    }

    private static string Pawns(params string[] pawns) => $$"""{"rulesVersion":1,"pawns":[{{string.Join(",", pawns)}}]}""";

    private static MatchRuleDecision RollKeeping(GameRoom room, Player player, string state, IRandomSource dice, object payload) =>
        Apply(room, player, ChogLifeMatchRules.Roll, state, dice, payload);

    private static int Mon(GameRoom room, Player viewer, string state, int seat) =>
        PawnView(room, viewer, state, seat).GetProperty("mon").GetInt32();

    [Fact]
    public void Double_dice_rolls_its_dice_at_once_for_everyone_to_see_and_keeps_the_turn()
    {
        var (room, players) = Started(LifeAbilities.DoubleDice);
        var dice = new Dice(2, 5);

        var rolled = Armed(room, players[0], LifeAbilities.DoubleDice, room.State!, dice);

        Assert.Equal([(1, 7), (1, 7)], dice.Draws);
        Assert.Equal("[2,5]", PawnView(room, players[1], rolled, 1).GetProperty("dice").GetRawText());
        Assert.False(PawnView(room, players[1], rolled, 1).TryGetProperty("armed", out _));
        Assert.False(View(room, players[0], rolled).TryGetProperty("lastMove", out _));
    }

    [Theory]
    [InlineData(2, """{"seat":1,"roll":2,"from":0,"to":2,"path":[1,2],"change":20,"dice":[2,5]}""")]
    [InlineData(5, """{"seat":1,"roll":5,"from":0,"to":5,"path":[1,2,3,4,5],"change":-15,"dice":[2,5]}""")]
    public void The_roll_after_double_dice_moves_by_the_die_kept_and_draws_no_die(int kept, string move)
    {
        var (room, players) = Started(LifeAbilities.DoubleDice);
        var rolled = Armed(room, players[0], LifeAbilities.DoubleDice, room.State!, new Dice(2, 5));
        var dice = new Dice();

        var decision = RollKeeping(room, players[0], rolled, dice, new { die = kept });

        Assert.Equal((MatchRuleOutcome.Accepted, true), (decision.Outcome, decision.EndsTurn));
        var state = decision.State!.Value.GetRawText();
        Assert.Equal(move, LastMove(state).GetRawText());
        Assert.Empty(dice.Draws);
        Assert.False(PawnView(room, players[0], state, 1).TryGetProperty("dice", out _));
    }

    [Fact]
    public void A_rare_double_dice_rolls_three_dice()
    {
        var (room, players) = StartedWith(Tiered(LifeAbilities.DoubleDice, new() { [LifeAbilities.Dice] = 3 }));
        var dice = new Dice(1, 6, 3);

        var rolled = Armed(room, players[0], LifeAbilities.DoubleDice, room.State!, dice);

        Assert.Equal(3, dice.Draws.Count);
        Assert.Equal("[1,6,3]", PawnView(room, players[1], rolled, 1).GetProperty("dice").GetRawText());
        Assert.Equal(3, LastMove(RollKeeping(room, players[0], rolled, new Dice(0), new { die = 3 }).State!.Value.GetRawText()).GetProperty("roll").GetInt32());
    }

    [Theory]
    [InlineData(null, "choose_die")]
    [InlineData("""{"die":"5"}""", "choose_die")]
    [InlineData("""{"die":4}""", "invalid_die")]
    [InlineData("""{"die":7}""", "invalid_die")]
    [InlineData("""{"die":5.5}""", "choose_die")]
    public void The_kept_die_must_be_one_the_server_rolled(string? payload, string reason)
    {
        var (room, players) = Started(LifeAbilities.DoubleDice);
        var rolled = Armed(room, players[0], LifeAbilities.DoubleDice, room.State!, new Dice(2, 5));
        var dice = new Dice(4);

        var decision = RollKeeping(room, players[0], rolled, dice, payload is null ? new { } : JsonDocument.Parse(payload).RootElement);

        Assert.Equal((MatchRuleOutcome.Rejected, reason, 0), (decision.Outcome, decision.Reason, dice.Draws.Count));
    }

    [Fact]
    public void A_roll_without_double_dice_ignores_a_die_in_the_payload()
    {
        var (room, players) = Started(LifeAbilities.RewardBoost);

        var decision = RollKeeping(room, players[0], room.State!, new Dice(1), new { die = 6 });

        Assert.Equal(1, LastMove(decision.State!.Value.GetRawText()).GetProperty("roll").GetInt32());
    }

    [Fact]
    public void Dice_already_rolled_cannot_be_rolled_again()
    {
        var (room, players) = StartedWith(Tiered(LifeAbilities.DoubleDice, new() { [LifeAbilities.Dice] = 2 }, uses: 2));
        var rolled = Armed(room, players[0], LifeAbilities.DoubleDice, room.State!, new Dice(2, 5));
        var dice = new Dice(1, 1);

        Assert.Equal(("dice_rolled", 0), (Use(room, players[0], LifeAbilities.DoubleDice, rolled, dice).Reason, dice.Draws.Count));
    }

    [Fact]
    public void A_turn_that_runs_out_after_double_dice_keeps_the_highest_for_the_next_roll()
    {
        var (room, players) = Started(LifeAbilities.DoubleDice);
        var rolled = Armed(room, players[0], LifeAbilities.DoubleDice, room.State!, new Dice(5, 2));

        using var document = JsonDocument.Parse(rolled);
        var dice = new Dice();
        var timedOut = Rules.TimeOutTurn(new MatchTurnContext(room, document.RootElement, dice))!.Value.GetRawText();

        Assert.Empty(dice.Draws);
        var pawn = PawnView(room, players[1], timedOut, 1);
        Assert.Equal((0, "[5,2]", 5), (pawn.GetProperty("tile").GetInt32(), pawn.GetProperty("dice").GetRawText(), pawn.GetProperty("kept").GetInt32()));

        // The next roll moves by the die kept, whatever the payload says.
        var decision = RollKeeping(room, players[0], timedOut, dice, new { die = 2 });
        Assert.Equal("""{"seat":1,"roll":5,"from":0,"to":5,"path":[1,2,3,4,5],"change":-15,"dice":[5,2]}""", LastMove(decision.State!.Value.GetRawText()).GetRawText());
        Assert.Empty(dice.Draws);
    }

    [Fact]
    public void A_turn_that_runs_out_with_the_die_already_kept_is_skipped()
    {
        var (room, players) = Started(LifeAbilities.DoubleDice);
        using var document = JsonDocument.Parse(State("""{"seat":1,"tile":0,"mon":100,"dice":[5,2],"kept":5}"""));

        Assert.Null(Rules.TimeOutTurn(new MatchTurnContext(room, document.RootElement, new Dice())));
    }

    [Theory]
    [InlineData("""{"seat":1,"tile":0,"mon":100,"dice":[5]}""")] // one die
    [InlineData("""{"seat":1,"tile":0,"mon":100,"dice":[1,2,3,4,5]}""")] // too many
    [InlineData("""{"seat":1,"tile":0,"mon":100,"dice":[0,5]}""")] // off the die
    [InlineData("""{"seat":1,"tile":0,"mon":100,"dice":[7,5]}""")]
    [InlineData("""{"seat":1,"tile":0,"mon":100,"dice":[2,5],"kept":4}""")] // kept a die not rolled
    [InlineData("""{"seat":1,"tile":0,"mon":100,"kept":4}""")] // kept without dice
    public void A_roll_with_dice_out_of_range_changes_nothing(string pawn)
    {
        var (room, players) = Started(LifeAbilities.DoubleDice);
        var dice = new Dice(2);

        var decision = RollKeeping(room, players[0], State(pawn), dice, new { die = 5 });

        Assert.Equal(("game_state_unavailable", 0), (decision.Reason, dice.Draws.Count));
    }

    [Theory]
    [InlineData(LifeAbilities.DoubleDice)]
    [InlineData(LifeAbilities.TaxTheRich)]
    public void Double_dice_and_tax_the_rich_cannot_be_used_while_choosing_a_lane(string ability)
    {
        var (room, players) = Started(ability);

        Assert.Equal(AbilityRefusal.NotNow, Use(room, players[0], ability, AtFork(room, players[0])).Reason);
    }

    [Fact]
    public void Tax_the_rich_takes_a_tenth_of_the_richest_opponents_liquid_mon_and_the_roll_tells_it()
    {
        var (room, players) = Started(LifeAbilities.TaxTheRich);
        var dice = new Dice();

        var taxed = Armed(room, players[0], LifeAbilities.TaxTheRich, State("""{"seat":1,"tile":0,"mon":100}""", """{"seat":2,"tile":0,"mon":305}"""), dice);

        Assert.Empty(dice.Draws);
        Assert.Equal((130, 275), (Mon(room, players[1], taxed, 1), Mon(room, players[1], taxed, 2)));
        Assert.Equal("""{"seat":1,"from":2,"amount":30}""", View(room, players[1], taxed).GetProperty("tax").GetRawText());

        var rolled = Roll(room, players[0], taxed, new Dice(2));
        Assert.Equal(
            """{"seat":1,"roll":2,"from":0,"to":2,"path":[1,2],"change":20,"taxed":{"seat":1,"from":2,"amount":30}}""",
            LastMove(rolled).GetRawText());
        Assert.False(View(room, players[1], rolled).TryGetProperty("tax", out _));
        Assert.Equal(150, Mon(room, players[1], rolled, 1));
    }

    [Theory]
    [InlineData(18, 1000, 50)] // capped
    [InlineData(20, 200, 40)]
    [InlineData(12, 99, 11)] // rounded down
    public void Tax_the_rich_takes_the_tiers_percent_at_most_the_cap(int percent, int richest, int taken)
    {
        var (room, players) = StartedWith(Tiered(LifeAbilities.TaxTheRich, new() { [LifeAbilities.Percent] = percent, [LifeAbilities.Cap] = 50 }));

        var taxed = Armed(room, players[0], LifeAbilities.TaxTheRich, State("""{"seat":1,"tile":0,"mon":10}""", $$"""{"seat":2,"tile":0,"mon":{{richest}}}"""));

        Assert.Equal((10 + taken, richest - taken), (Mon(room, players[1], taxed, 1), Mon(room, players[1], taxed, 2)));
    }

    [Fact]
    public void The_richest_is_by_total_portfolio_and_only_liquid_mon_is_taken()
    {
        var (room, players) = StartedWith(Catalog.Definitions.Single(d => d.Id == LifeAbilities.TaxTheRich), count: 3);

        // Seat 2 holds the most liquid MON, but seat 3's bonuses and stake make it the richest.
        var taxed = Armed(room, players[0], LifeAbilities.TaxTheRich, Pawns(
            """{"seat":1,"tile":0,"mon":100}""",
            """{"seat":2,"tile":0,"mon":300}""",
            """{"seat":3,"tile":10,"mon":200,"stake":{"lane":"Safe","amount":50},"bonuses":100}"""));

        var rich = PawnView(room, players[1], taxed, 3);
        Assert.Equal((120, 300), (Mon(room, players[1], taxed, 1), Mon(room, players[1], taxed, 2)));
        Assert.Equal((180, 50, 100, 330), (rich.GetProperty("mon").GetInt32(), rich.GetProperty("assets").GetInt32(), rich.GetProperty("bonuses").GetInt32(), rich.GetProperty("portfolio").GetInt32()));
    }

    [Fact]
    public void A_tie_for_richest_takes_from_the_first_by_seat()
    {
        var (room, players) = StartedWith(Catalog.Definitions.Single(d => d.Id == LifeAbilities.TaxTheRich), count: 3);

        var taxed = Armed(room, players[0], LifeAbilities.TaxTheRich, Pawns(
            """{"seat":1,"tile":0,"mon":100}""", """{"seat":2,"tile":0,"mon":300}""", """{"seat":3,"tile":0,"mon":300}"""));

        Assert.Equal((270, 300), (Mon(room, players[1], taxed, 2), Mon(room, players[1], taxed, 3)));
    }

    [Fact]
    public void A_finished_opponent_can_be_taxed_but_one_who_left_cannot()
    {
        var (room, players) = StartedWith(Catalog.Definitions.Single(d => d.Id == LifeAbilities.TaxTheRich), count: 3);
        room.Leave(players[1].Id, Now.AddMinutes(2));

        var state = Pawns("""{"seat":1,"tile":0,"mon":100}""", """{"seat":2,"tile":0,"mon":900}""", """{"seat":3,"tile":35,"mon":200}""")
            .Replace("]}", "],\"finished\":[3]}", StringComparison.Ordinal);
        var taxed = Armed(room, players[0], LifeAbilities.TaxTheRich, state);

        Assert.Equal((120, 180), (Mon(room, players[0], taxed, 1), Mon(room, players[0], taxed, 3)));
    }

    [Theory]
    [InlineData("""{"seat":1,"tile":0,"mon":300}""", """{"seat":2,"tile":0,"mon":300}""", "already_richest")]
    [InlineData("""{"seat":1,"tile":0,"mon":400}""", """{"seat":2,"tile":0,"mon":300}""", "already_richest")]
    [InlineData("""{"seat":1,"tile":0,"mon":100,"bonuses":300}""", """{"seat":2,"tile":0,"mon":350}""", "already_richest")]
    [InlineData("""{"seat":1,"tile":0,"mon":100}""", """{"seat":2,"tile":0,"mon":0,"bonuses":500}""", "nothing_to_tax")]
    [InlineData("""{"seat":1,"tile":0,"mon":100}""", """{"seat":2,"tile":0,"mon":9,"bonuses":500}""", "nothing_to_tax")]
    public void Tax_the_rich_is_refused_without_a_richer_opponent_with_mon_to_take(string pawn1, string pawn2, string reason)
    {
        var (room, players) = Started(LifeAbilities.TaxTheRich);
        var dice = new Dice();

        Assert.Equal((reason, 0), (Use(room, players[0], LifeAbilities.TaxTheRich, State(pawn1, pawn2), dice).Reason, dice.Draws.Count));
    }

    [Fact]
    public void Taxed_mon_counts_in_the_final_standings()
    {
        var (room, players) = Started(LifeAbilities.TaxTheRich);
        var taxed = Armed(room, players[0], LifeAbilities.TaxTheRich, State("""{"seat":1,"tile":0,"mon":100}""", """{"seat":2,"tile":0,"mon":110}"""));

        using var document = JsonDocument.Parse(taxed);
        var ending = Rules.EndMatch(new MatchEndContext(room, document.RootElement, TimeUp: true))!;

        // 111 to 99: the tax turned a 10 MON deficit into a lead.
        Assert.Equal(
            [(players[0].Id, 1, 111), (players[1].Id, 2, 99)],
            ending.Standings.Select(s => (s.PlayerId, s.Rank, s.Result!.Value.GetProperty("portfolio").GetInt32())));
    }
}
