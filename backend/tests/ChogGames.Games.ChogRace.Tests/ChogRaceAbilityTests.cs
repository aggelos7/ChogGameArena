using System.Text.Json;
using ChogGames.Core.Abilities;
using ChogGames.Core.Games;
using ChogGames.Core.Matches;
using ChogGames.Core.Players;
using ChogGames.Core.Rooms;
using ChogGames.Games.ChogRace.Abilities;
using ChogGames.Games.ChogRace.Rules;

namespace ChogGames.Games.ChogRace.Tests;

/// <summary>
/// STORY-055, DEC-059: Chog Race's four abilities through the shared engine, each used once on the player's turn. Speed
/// Boost adds 2 tiles to that turn's roll. Shield stays up, for all to see, until it blocks the next Rug, Liquidated or
/// Whale aimed at its player, at once and with their HODL kept. Card Vision shows only its player the deck's top card
/// until a draw. Card Steal moves a random card from an opponent's hand into the player's, which needs room; only the two
/// of them see which card. STORY-090, DEC-077: with a holder Tier's locked values, Speed Boost adds more tiles, Shield
/// blocks more attacks, Card Vision shows more cards and Card Steal takes more.
/// </summary>
public class ChogRaceAbilityTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    private static readonly ChogRaceMatchRules Rules = new([RaceRulesV1.Rules]);

    private static readonly AbilityCatalog Catalog = RaceAbilities.Catalog(Rules);

    private static readonly AbilityEngine Engine = new([Catalog]);

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

    /// <summary>A started match of <paramref name="abilities"/>.Length players, seat n having locked abilities[n - 1].</summary>
    private static (GameRoom Room, Player[] Players) Started(params string[] abilities) =>
        StartedWith([.. abilities.Select(id => Catalog.Definitions.Single(d => d.Id == id))]);

    /// <summary>The catalog's ability with a holder Tier's <paramref name="values"/> (STORY-090, DEC-077).</summary>
    private static AbilityDefinition Tiered(string id, string name, decimal value)
    {
        var defined = Catalog.Definitions.Single(d => d.Id == id);
        return defined.WithValues(new Dictionary<string, decimal> { [name] = value }, defined.MaxUses);
    }

    /// <summary>A started match where seat n locked <paramref name="definitions"/>[n - 1], with its values.</summary>
    private static (GameRoom Room, Player[] Players) StartedWith(params AbilityDefinition[] definitions)
    {
        var abilities = definitions.Select(d => d.Id).ToArray();
        var players = abilities.Select((_, i) =>
        {
            Assert.True(PlayerDisplayName.TryCreate($"Player {i + 1}", out var name));
            return Player.CreateGuest(name, Now);
        }).ToArray();
        Assert.True(RoomCode.TryParse("R4CE7Q", out var code));
        var room = GameRoom.Create(GameDefinition.Create(GameDefinition.ChogRace, 2, 6), players[0], code, Now);
        for (var i = 1; i < players.Length; i++)
        {
            room.Join(players[i], Now.AddSeconds(i));
        }

        for (var i = 0; i < players.Length; i++)
        {
            room.SelectAbility(players[i].Id, abilities[i]);
            room.MarkReady(players[i]);
        }

        var random = new KeepOrder();
        room.Start(
            players[0].Id,
            players,
            players.ToDictionary(p => p.Id, _ => CharacterSnapshot.Standard(DefaultAvatar.Classic, Now)),
            TimeSpan.FromSeconds(20),
            random,
            Now.AddMinutes(1),
            players.Select((p, i) => (p.Id, Snapshot: AbilitySnapshot.Lock(Catalog, definitions[i]))).ToDictionary(p => p.Id, p => p.Snapshot),
            started => Rules.StartState(new MatchStartContext(started, random))?.GetRawText());
        return (room, players);
    }

    private static AbilityResult Use(GameRoom room, Player player, string state, int? target = null, IRandomSource? random = null)
    {
        using var document = JsonDocument.Parse(state);
        var payload = target is null
            ? JsonSerializer.SerializeToElement(new { abilityId = room.Members.Single(m => m.PlayerId == player.Id).Ability!.Id })
            : JsonSerializer.SerializeToElement(new { abilityId = room.Members.Single(m => m.PlayerId == player.Id).Ability!.Id, target });
        return Engine.Use(new AbilityRequest(room, player.Id, payload, document.RootElement.Clone(), random ?? new Dice()));
    }

    /// <summary>Uses the player's ability and returns the new state; the use must be accepted.</summary>
    private static string Used(GameRoom room, Player player, string state, int? target = null, IRandomSource? random = null)
    {
        var result = Use(room, player, state, target, random);
        Assert.True(result.Used, result.Reason);
        return result.State!.Value.GetRawText();
    }

    private static MatchRuleDecision Apply(GameRoom room, Player player, string type, string state, IRandomSource random, string payload = "{}")
    {
        using var stateJson = JsonDocument.Parse(state);
        using var payloadJson = JsonDocument.Parse(payload);
        var decision = Rules.Apply(new MatchCommandContext(room, player.Id, type, payloadJson.RootElement, stateJson.RootElement, random));
        return decision with { State = decision.State?.Clone() };
    }

    private static JsonElement View(GameRoom room, Player viewer, string state)
    {
        using var document = JsonDocument.Parse(state);
        return Rules.ViewFor(new MatchViewContext(room, viewer.Id, document.RootElement))!.Value.Clone();
    }

    private static JsonElement Pawn(string state, int seat) =>
        JsonDocument.Parse(state).RootElement.GetProperty("pawns").EnumerateArray().Single(p => p.GetProperty("seat").GetInt32() == seat).Clone();

    private static JsonElement PawnView(GameRoom room, Player viewer, string state, int seat) =>
        View(room, viewer, state).GetProperty("pawns").EnumerateArray().Single(p => p.GetProperty("seat").GetInt32() == seat);

    private static string[] Cards(JsonElement cards) => cards.EnumerateArray().Select(card => card.GetString()!).ToArray();

    private static string[] HandOf(string state, int seat) =>
        Pawn(state, seat).TryGetProperty("hand", out var hand) ? Cards(hand) : [];

    /// <summary>
    /// A version 1 state with pawns on <paramref name="tiles"/>, by seat from 1, holding <paramref name="hands"/>, and
    /// the <paramref name="deck"/>; with <paramref name="playPhase"/>, that seat rolled from tile 0 and is in its Play phase.
    /// </summary>
    private static string State(int[] tiles, string[][]? hands = null, string[]? deck = null, int? playPhase = null, string pawnExtra = "")
    {
        var pawns = tiles.Select((tile, i) =>
        {
            var hand = hands?[i] is { Length: > 0 } cards ? $$""","hand":{{JsonSerializer.Serialize(cards)}}""" : "";
            var extra = i == 0 ? pawnExtra : "";
            return $$"""{"seat":{{i + 1}},"tile":{{tile}}{{hand}}{{extra}}}""";
        });
        var phase = playPhase is { } seat
            ? $$""","playPhase":{{seat}},"lastMove":{"seat":{{seat}},"roll":1,"from":0,"landed":{{tiles[seat - 1]}},"to":{{tiles[seat - 1]}}}"""
            : "";
        return $$"""{"rulesVersion":1,"pawns":[{{string.Join(",", pawns)}}],"deck":{{JsonSerializer.Serialize(deck ?? [])}},"discard":[]{{phase}}}""";
    }

    [Fact]
    public void The_catalog_offers_the_four_race_abilities_with_their_windows()
    {
        Assert.Equal((GameDefinition.ChogRace, 2), (Catalog.GameType, Catalog.Version));
        Assert.Equal(
            [
                ("movement_boost", "Speed Boost", AbilityTargetKind.None, "before_roll"),
                ("attack_block", "Shield", AbilityTargetKind.None, "before_roll,after_roll"),
                ("card_preview", "Card Vision", AbilityTargetKind.None, "before_roll,after_roll"),
                ("card_steal", "Card Steal", AbilityTargetKind.Opponent, "before_roll,after_roll"),
            ],
            Catalog.Definitions.Select(d => (d.Id, d.Name, d.Target, string.Join(",", d.Windows))));
        Assert.All(Catalog.Definitions, d => Assert.Equal(1, d.MaxUses));
        Assert.Equal(
            ["tiles=2", "blocks=1", "cards=1", "cards=1"],
            Catalog.Definitions.Select(d => string.Join(",", d.Parameters.Select(p => $"{p.Key}={p.Value}"))));
    }

    [Fact]
    public void Speed_boost_adds_its_tiles_to_the_roll_and_is_spent()
    {
        var (room, players) = Started(RaceAbilities.MovementBoost, RaceAbilities.AttackBlock);
        var boosted = Used(room, players[0], State([0, 0]));

        Assert.Equal(2, Pawn(boosted, 1).GetProperty("boost").GetInt32());
        Assert.Equal(2, PawnView(room, players[1], boosted, 1).GetProperty("boost").GetInt32());
        Assert.Equal("""{"seat":1,"ability":"movement_boost"}""", View(room, players[1], boosted).GetProperty("ability").GetRawText());

        var dice = new Dice(3);
        var rolled = Apply(room, players[0], ChogRaceMatchRules.Roll, boosted, dice);

        Assert.Equal([(1, 4)], dice.Draws);
        Assert.Equal(
            """{"seat":1,"roll":3,"from":0,"landed":5,"to":5,"boost":2}""",
            rolled.State!.Value.GetProperty("lastMove").GetRawText());
        Assert.False(Pawn(rolled.State.Value.GetRawText(), 1).TryGetProperty("boost", out _));
    }

    [Fact]
    public void A_boosted_roll_resolves_the_tile_it_lands_on_and_stops_on_the_finish()
    {
        var (room, players) = Started(RaceAbilities.MovementBoost, RaceAbilities.AttackBlock);

        // 2 + 2 lands on the Boost at tile 4, which jumps to 6.
        var jumped = Apply(room, players[0], ChogRaceMatchRules.Roll, Used(room, players[0], State([0, 0])), new Dice(2));
        Assert.Equal(
            """{"seat":1,"roll":2,"from":0,"landed":4,"to":6,"effect":"Boost","boost":2}""",
            jumped.State!.Value.GetProperty("lastMove").GetRawText());

        // From 24, 3 + 2 would pass the Finish at 27: it stops there and wins.
        var won = Apply(room, players[0], ChogRaceMatchRules.Roll, Used(room, players[0], State([24, 0])), new Dice(3));
        Assert.NotNull(won.Standings);
        Assert.Equal(1, won.State!.Value.GetProperty("winner").GetInt32());
    }

    [Fact]
    public void Speed_boost_is_only_used_before_the_roll()
    {
        var (room, players) = Started(RaceAbilities.MovementBoost, RaceAbilities.AttackBlock);

        Assert.Equal(AbilityRefusal.NotNow, Use(room, players[0], State([3, 0], [["Pump"], []], playPhase: 1)).Reason);
    }

    [Fact]
    public void Shield_blocks_the_next_attack_at_once_keeping_the_targets_cards()
    {
        var (room, players) = Started(RaceAbilities.AttackBlock, RaceAbilities.CardSteal);
        var shielded = Used(room, players[0], State([8, 3], [["Hodl"], ["Rug"]], playPhase: 1));

        Assert.True(Pawn(shielded, 1).GetProperty("shielded").GetBoolean());
        Assert.True(PawnView(room, players[1], shielded, 1).GetProperty("shielded").GetBoolean());

        // Seat 2's turn: they rolled onto tile 5 and play Rug on seat 1.
        var attack = State([8, 5], [["Hodl"], ["Rug"]], playPhase: 2, pawnExtra: ",\"shielded\":true");
        var decision = Apply(room, players[1], ChogRaceMatchRules.PlayCard, attack, new Dice(), """{"card":"Rug","target":1}""");

        Assert.Equal((MatchRuleOutcome.Accepted, true, (Guid?)null, (Guid?)null), (decision.Outcome, decision.EndsTurn, decision.AwaitsReplyFrom, decision.SkipsNextTurnOf));
        var state = decision.State!.Value.GetRawText();
        Assert.Equal(8, Pawn(state, 1).GetProperty("tile").GetInt32());
        Assert.False(Pawn(state, 1).TryGetProperty("shielded", out _));
        Assert.Equal(["Hodl"], HandOf(state, 1));
        Assert.Equal(["Rug"], Cards(decision.State.Value.GetProperty("discard")));
        Assert.Equal("""{"card":"Rug","target":1,"reply":"Shielded"}""", decision.State.Value.GetProperty("lastMove").GetProperty("play").GetRawText());
    }

    [Theory]
    [InlineData("Liquidated")]
    [InlineData("Whale")]
    public void Shield_blocks_liquidated_and_whale_too(string card)
    {
        var (room, players) = Started(RaceAbilities.CardSteal, RaceAbilities.AttackBlock);
        var state = State([5, 9], [[card], []], playPhase: 1).Replace("{\"seat\":2,\"tile\":9}", "{\"seat\":2,\"tile\":9,\"shielded\":true}");

        var decision = Apply(room, players[0], ChogRaceMatchRules.PlayCard, state, new Dice(), $$"""{"card":"{{card}}","target":2}""");

        Assert.Equal((MatchRuleOutcome.Accepted, true, (Guid?)null), (decision.Outcome, decision.EndsTurn, decision.SkipsNextTurnOf));
        Assert.Equal("Shielded", decision.State!.Value.GetProperty("lastMove").GetProperty("play").GetProperty("reply").GetString());
        Assert.Equal([5, 9], decision.State.Value.GetProperty("pawns").EnumerateArray().Select(p => p.GetProperty("tile").GetInt32()));
    }

    [Fact]
    public void Shield_does_not_block_reverse_and_stays_up()
    {
        var (room, players) = Started(RaceAbilities.CardSteal, RaceAbilities.AttackBlock);
        var state = State([5, 10], [["Reverse"], []], playPhase: 1).Replace("{\"seat\":2,\"tile\":10}", "{\"seat\":2,\"tile\":10,\"shielded\":true}");

        var decision = Apply(room, players[0], ChogRaceMatchRules.PlayCard, state, new Dice(), """{"card":"Reverse"}""");

        var after = decision.State!.Value.GetRawText();
        // Back 1 onto the Setback at 9, which sends it on to 7.
        Assert.Equal(7, Pawn(after, 2).GetProperty("tile").GetInt32());
        Assert.True(Pawn(after, 2).GetProperty("shielded").GetBoolean());
    }

    [Fact]
    public void Card_vision_shows_only_its_player_the_top_card_until_it_is_drawn()
    {
        var (room, players) = Started(RaceAbilities.CardPreview, RaceAbilities.AttackBlock, RaceAbilities.CardSteal);
        var seen = Used(room, players[0], State([0, 0, 0], deck: ["Whale", "Pump"]));

        Assert.Equal("Whale", View(room, players[0], seen).GetProperty("next").GetString());
        Assert.False(View(room, players[1], seen).TryGetProperty("next", out _));
        Assert.False(View(room, players[2], seen).TryGetProperty("next", out _));
        Assert.Equal("""{"seat":1,"ability":"card_preview"}""", View(room, players[1], seen).GetProperty("ability").GetRawText());

        // A roll that draws nothing keeps it shown; landing on the Card tile at 2 draws the Whale and ends it.
        var walked = Apply(room, players[0], ChogRaceMatchRules.Roll, seen, new Dice(1)).State!.Value.GetRawText();
        Assert.Equal("Whale", View(room, players[0], walked).GetProperty("next").GetString());
        var drawn = Apply(room, players[0], ChogRaceMatchRules.Roll, walked, new Dice(1)).State!.Value.GetRawText();
        Assert.Equal(["Whale"], HandOf(drawn, 1));
        Assert.False(View(room, players[0], drawn).TryGetProperty("next", out _));
        Assert.False(Pawn(drawn, 1).TryGetProperty("sees", out _));
    }

    [Fact]
    public void Card_vision_ends_when_another_player_draws_the_card()
    {
        var (room, players) = Started(RaceAbilities.CardPreview, RaceAbilities.AttackBlock);
        var seen = Used(room, players[0], State([0, 1], deck: ["Whale", "Pump"]));

        var drawn = Apply(room, players[1], ChogRaceMatchRules.Roll, seen, new Dice(1)).State!.Value.GetRawText();

        Assert.Equal(["Whale"], HandOf(drawn, 2));
        Assert.False(View(room, players[0], drawn).TryGetProperty("next", out _));
    }

    [Fact]
    public void An_epic_speed_boost_moves_its_locked_tiles()
    {
        var (room, players) = StartedWith(Tiered(RaceAbilities.MovementBoost, RaceAbilities.Tiles, 4), Catalog.Definitions[1]);
        var boosted = Used(room, players[0], State([0, 0]));

        Assert.Equal(4, PawnView(room, players[1], boosted, 1).GetProperty("boost").GetInt32());
        var rolled = Apply(room, players[0], ChogRaceMatchRules.Roll, boosted, new Dice(1));

        // 1 + 4 lands on tile 5.
        Assert.Equal(
            """{"seat":1,"roll":1,"from":0,"landed":5,"to":5,"boost":4}""",
            rolled.State!.Value.GetProperty("lastMove").GetRawText());
    }

    [Fact]
    public void A_rare_shield_blocks_two_attacks_then_is_spent()
    {
        var (room, players) = StartedWith(Tiered(RaceAbilities.AttackBlock, RaceAbilities.Blocks, 2), Catalog.Definitions[3]);
        var shielded = Used(room, players[0], State([8, 3], [["Hodl"], ["Rug", "Rug"]], playPhase: 1));
        Assert.Equal(2, Pawn(shielded, 1).GetProperty("blocks").GetInt32());
        Assert.Equal(2, PawnView(room, players[1], shielded, 1).GetProperty("blocks").GetInt32());

        // Seat 2 rolled onto tile 5 and plays Rug on seat 1, twice over two turns.
        var first = Apply(
            room, players[1], ChogRaceMatchRules.PlayCard,
            State([8, 5], [["Hodl"], ["Rug", "Rug"]], playPhase: 2, pawnExtra: ",\"shielded\":true,\"blocks\":2"),
            new Dice(), """{"card":"Rug","target":1}""").State!.Value.GetRawText();
        Assert.Equal((8, true, 1), (Pawn(first, 1).GetProperty("tile").GetInt32(), Pawn(first, 1).GetProperty("shielded").GetBoolean(), Pawn(first, 1).GetProperty("blocks").GetInt32()));
        Assert.Equal(1, PawnView(room, players[0], first, 1).GetProperty("blocks").GetInt32());

        var second = Apply(
            room, players[1], ChogRaceMatchRules.PlayCard,
            State([8, 5], [["Hodl"], ["Rug"]], playPhase: 2, pawnExtra: ",\"shielded\":true,\"blocks\":1"),
            new Dice(), """{"card":"Rug","target":1}""").State!.Value.GetRawText();
        Assert.Equal(8, Pawn(second, 1).GetProperty("tile").GetInt32());
        Assert.False(Pawn(second, 1).TryGetProperty("shielded", out _));
        Assert.False(Pawn(second, 1).TryGetProperty("blocks", out _));
        Assert.Equal("Shielded", JsonDocument.Parse(second).RootElement.GetProperty("lastMove").GetProperty("play").GetProperty("reply").GetString());
    }

    [Fact]
    public void A_shield_raised_before_tier_values_blocks_once()
    {
        var (room, players) = Started(RaceAbilities.CardSteal, RaceAbilities.AttackBlock);
        var state = State([5, 9], [["Rug"], []], playPhase: 1).Replace("{\"seat\":2,\"tile\":9}", "{\"seat\":2,\"tile\":9,\"shielded\":true}");

        var after = Apply(room, players[0], ChogRaceMatchRules.PlayCard, state, new Dice(), """{"card":"Rug","target":2}""").State!.Value.GetRawText();

        Assert.False(Pawn(after, 2).TryGetProperty("shielded", out _));
    }

    [Fact]
    public void A_legendary_card_vision_shows_the_top_three_cards_until_a_draw()
    {
        var (room, players) = StartedWith(Tiered(RaceAbilities.CardPreview, RaceAbilities.Cards, 3), Catalog.Definitions[1]);
        var seen = Used(room, players[0], State([0, 1], deck: ["Whale", "Pump", "Rug", "Hodl"]));

        Assert.Equal("Whale", View(room, players[0], seen).GetProperty("next").GetString());
        Assert.Equal(["Whale", "Pump", "Rug"], Cards(View(room, players[0], seen).GetProperty("seen")));
        Assert.False(View(room, players[1], seen).TryGetProperty("seen", out _));

        var drawn = Apply(room, players[1], ChogRaceMatchRules.Roll, seen, new Dice(1)).State!.Value.GetRawText();
        Assert.False(View(room, players[0], drawn).TryGetProperty("seen", out _));
        Assert.False(Pawn(drawn, 1).TryGetProperty("seesCards", out _));
    }

    [Fact]
    public void A_short_deck_shows_only_the_cards_it_has()
    {
        var (room, players) = StartedWith(Tiered(RaceAbilities.CardPreview, RaceAbilities.Cards, 3), Catalog.Definitions[1]);
        var seen = Used(room, players[0], State([0, 0], deck: ["Pump"]));

        Assert.Equal(["Pump"], Cards(View(room, players[0], seen).GetProperty("seen")));
    }

    [Fact]
    public void An_epic_card_steal_takes_two_cards_seen_only_by_the_two_players()
    {
        var (room, players) = StartedWith(Tiered(RaceAbilities.CardSteal, RaceAbilities.Cards, 2), Catalog.Definitions[1], Catalog.Definitions[2]);
        var dice = new Dice(1, 0);
        var stolen = Used(room, players[0], State([0, 0, 0], [[], ["Rug", "Hodl", "Fomo"], []]), target: 2, random: dice);

        Assert.Equal([(0, 3), (0, 2)], dice.Draws);
        Assert.Equal(["Hodl", "Rug"], HandOf(stolen, 1));
        Assert.Equal(["Fomo"], HandOf(stolen, 2));
        Assert.Equal(
            """{"seat":1,"ability":"card_steal","target":2,"card":"Hodl","cards":["Hodl","Rug"],"taken":2}""",
            View(room, players[1], stolen).GetProperty("ability").GetRawText());
        Assert.Equal(
            """{"seat":1,"ability":"card_steal","target":2,"taken":2}""",
            View(room, players[2], stolen).GetProperty("ability").GetRawText());
    }

    [Fact]
    public void Card_steal_takes_no_more_than_the_target_holds_or_the_hand_has_room_for()
    {
        var (room, players) = StartedWith(Tiered(RaceAbilities.CardSteal, RaceAbilities.Cards, 2), Catalog.Definitions[1]);

        var fromOne = Used(room, players[0], State([0, 0], [[], ["Rug"]]), target: 2, random: new Dice(0));
        Assert.Equal(["Rug"], HandOf(fromOne, 1));

        // A hand of 3 holds 2 already: room for one more.
        var roomForOne = Used(room, players[0], State([0, 0], [["Pump", "Fomo"], ["Rug", "Hodl"]]), target: 2, random: new Dice(1));
        Assert.Equal(["Pump", "Fomo", "Hodl"], HandOf(roomForOne, 1));
        Assert.Equal(["Rug"], HandOf(roomForOne, 2));
    }

    [Fact]
    public void Card_vision_needs_a_card_in_the_deck()
    {
        var (room, players) = Started(RaceAbilities.CardPreview, RaceAbilities.AttackBlock);
        var state = State([0, 0], deck: []);

        Assert.Equal("deck_empty", Use(room, players[0], state).Reason);
    }

    [Fact]
    public void Card_steal_takes_a_random_card_seen_only_by_the_two_players()
    {
        var (room, players) = Started(RaceAbilities.CardSteal, RaceAbilities.AttackBlock, RaceAbilities.CardPreview);
        var dice = new Dice(1);
        var stolen = Used(room, players[0], State([0, 0, 0], [["Pump"], ["Rug", "Hodl", "Fomo"], []]), target: 2, random: dice);

        Assert.Equal([(0, 3)], dice.Draws);
        Assert.Equal(["Pump", "Hodl"], HandOf(stolen, 1));
        Assert.Equal(["Rug", "Fomo"], HandOf(stolen, 2));
        Assert.Equal(
            """{"seat":1,"ability":"card_steal","target":2,"card":"Hodl","cards":["Hodl"],"taken":1}""",
            View(room, players[0], stolen).GetProperty("ability").GetRawText());
        Assert.Equal(
            """{"seat":1,"ability":"card_steal","target":2,"card":"Hodl","cards":["Hodl"],"taken":1}""",
            View(room, players[1], stolen).GetProperty("ability").GetRawText());
        Assert.Equal(
            """{"seat":1,"ability":"card_steal","target":2,"taken":1}""",
            View(room, players[2], stolen).GetProperty("ability").GetRawText());
        Assert.Equal([2, 2, 0], View(room, players[2], stolen).GetProperty("pawns").EnumerateArray().Select(p => p.GetProperty("cards").GetInt32()));
    }

    [Fact]
    public void Card_steal_that_empties_a_hand_and_the_use_is_forgotten_when_another_player_rolls()
    {
        var (room, players) = Started(RaceAbilities.CardSteal, RaceAbilities.AttackBlock);
        var stolen = Used(room, players[0], State([0, 0], [[], ["Rug"]], playPhase: null), target: 2, random: new Dice(0));
        Assert.Equal(["Rug"], HandOf(stolen, 1));
        Assert.False(Pawn(stolen, 2).TryGetProperty("hand", out _));

        // Seat 1's own roll keeps it; seat 2's clears it.
        var own = Apply(room, players[0], ChogRaceMatchRules.Roll, stolen, new Dice(1));
        var kept = own.State!.Value.GetRawText().Replace("\"playPhase\":1,", "");
        Assert.True(JsonDocument.Parse(kept).RootElement.TryGetProperty("ability", out _));
        var next = Apply(room, players[1], ChogRaceMatchRules.Roll, kept, new Dice(1));
        Assert.False(next.State!.Value.TryGetProperty("ability", out _));
    }

    [Theory]
    [InlineData(new[] { "Pump", "Rug", "Fomo" }, new[] { "Hodl" }, 2, "hand_full")]
    [InlineData(new string[0], new string[0], 2, "invalid_target")]
    [InlineData(new string[0], new[] { "Hodl" }, 1, "invalid_target")]
    [InlineData(new string[0], new[] { "Hodl" }, 3, "invalid_target")]
    public void Card_steal_needs_room_and_an_opponent_holding_a_card(string[] mine, string[] theirs, int target, string reason)
    {
        var (room, players) = Started(RaceAbilities.CardSteal, RaceAbilities.AttackBlock);
        var state = State([0, 0], [mine, theirs]);

        Assert.Equal(reason, Use(room, players[0], state, target).Reason);
    }

    [Fact]
    public void Card_steal_needs_a_target()
    {
        var (room, players) = Started(RaceAbilities.CardSteal, RaceAbilities.AttackBlock);

        Assert.Equal(AbilityRefusal.InvalidTarget, Use(room, players[0], State([0, 0], [[], ["Hodl"]])).Reason);
    }

    [Theory]
    [InlineData(RaceAbilities.AttackBlock)]
    [InlineData(RaceAbilities.CardPreview)]
    [InlineData(RaceAbilities.CardSteal)]
    public void Shield_vision_and_steal_are_used_before_or_after_the_roll(string ability)
    {
        var (room, players) = Started(ability, RaceAbilities.AttackBlock);
        int? target = ability == RaceAbilities.CardSteal ? 2 : null;

        Assert.True(Use(room, players[0], State([0, 0], [["Pump"], ["Rug"]], deck: ["Fomo"]), target, new Dice(0)).Used);
        Assert.True(Use(room, players[0], State([3, 0], [["Pump"], ["Rug"]], deck: ["Fomo"], playPhase: 1), target, new Dice(0)).Used);
    }

    [Theory]
    [InlineData("""{"rulesVersion":1,"pawns":[{"seat":1,"tile":27},{"seat":2,"tile":0}],"winner":1}""", AbilityRefusal.NotNow)]
    [InlineData("""{"rulesVersion":9,"pawns":[{"seat":1,"tile":0},{"seat":2,"tile":0}]}""", AbilityRefusal.NotNow)]
    [InlineData("""{"rulesVersion":1,"pawns":[{"seat":1,"tile":0,"boost":7},{"seat":2,"tile":0}]}""", AbilityRefusal.NotNow)]
    public void No_ability_is_used_once_the_race_is_won_or_on_a_state_this_server_cant_play(string state, string reason)
    {
        var (room, players) = Started(RaceAbilities.AttackBlock, RaceAbilities.CardSteal);

        Assert.Equal(reason, Use(room, players[0], state).Reason);
    }

    [Fact]
    public void A_roll_with_a_malformed_boost_is_refused()
    {
        var (room, players) = Started(RaceAbilities.MovementBoost, RaceAbilities.AttackBlock);

        var decision = Apply(room, players[0], ChogRaceMatchRules.Roll, State([0, 0], pawnExtra: ",\"boost\":7"), new Dice());

        Assert.Equal((MatchRuleOutcome.Rejected, "game_state_unavailable"), (decision.Outcome, decision.Reason));
    }
}
