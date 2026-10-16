using System.Text.Json;
using ChogGames.Core.Games;
using ChogGames.Core.Matches;
using ChogGames.Core.Players;
using ChogGames.Core.Rooms;
using ChogGames.Games.ChogRace.Rules;
using static ChogGames.Games.ChogRace.Rules.RaceTile;

namespace ChogGames.Games.ChogRace.Tests;

/// <summary>
/// STORY-050, DEC-054: a Chog Race match starts with every pawn on the start tile under the current rules version, and
/// every player sees that version's track and the pawns of the players still playing. STORY-051, DEC-055: on their turn a
/// player rolls in the Move phase; the server rolls, moves the pawn and resolves the tile it lands on, and the turn ends
/// unless they hold a card they can play (none yet). Commands out of phase are rejected; the first to reach the Finish
/// wins. STORY-052, DEC-056: Card tiles draw from a deck the server shuffled, up to a full hand, refilling an empty deck
/// from the discard pile and leavers' hands; each player sees only their own cards. STORY-053, DEC-057: holding a
/// movement or chaos card opens the Play phase, where <c>PLAY_CARD</c> plays one, onto the discard pile, and ends the turn.
/// STORY-054, DEC-058: Rug and Liquidated are played on an opponent; an attack (with Whale) on a player holding cards waits
/// for their reply, HODL to block it once or <c>PASS</c> to let it land, and lands by itself when the reply runs out.
/// </summary>
public class ChogRaceMatchRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    private static readonly ChogRaceMatchRules Rules = new([RaceRulesV1.Rules]);

    /// <summary>Never reorders a shuffle, so seats follow join order (host first).</summary>
    private sealed class KeepOrder : IRandomSource
    {
        public int Next(int minInclusive, int maxExclusive) => maxExclusive - 1;
    }

    /// <summary>Fails the test if the rules draw anything.</summary>
    private sealed class NoDraws : IRandomSource
    {
        public int Next(int minInclusive, int maxExclusive) => throw new InvalidOperationException("Nothing should be drawn.");
    }

    private static Player Guest(int n)
    {
        Assert.True(PlayerDisplayName.TryCreate($"Player {n}", out var name));
        return Player.CreateGuest(name, Now);
    }

    /// <summary>A started Chog Race match of <paramref name="count"/> players, with the rules' start state stored.</summary>
    private static (GameRoom Room, Player[] Players) Started(int count, ChogRaceMatchRules? rules = null)
    {
        rules ??= Rules;
        var players = Enumerable.Range(1, count).Select(Guest).ToArray();
        Assert.True(RoomCode.TryParse("R4CE7Q", out var code));
        var room = GameRoom.Create(GameDefinition.Create(GameDefinition.ChogRace, 2, 6), players[0], code, Now);
        for (var i = 1; i < count; i++)
        {
            room.Join(players[i], Now.AddSeconds(i));
        }

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
            startState: started => rules.StartState(new MatchStartContext(started, random))?.GetRawText());
        return (room, players);
    }

    private static JsonElement? ViewOf(GameRoom room, Guid viewer, string state, ChogRaceMatchRules? rules = null)
    {
        using var document = JsonDocument.Parse(state);
        return (rules ?? Rules).ViewFor(new MatchViewContext(room, viewer, document.RootElement))?.Clone();
    }

    private static JsonElement View(GameRoom room, Guid viewer, ChogRaceMatchRules? rules = null) =>
        ViewOf(room, viewer, room.State!, rules) ?? throw new Xunit.Sdk.XunitException("No view.");

    private static (int Seat, int Tile)[] Pawns(JsonElement view) =>
        view.GetProperty("pawns").EnumerateArray()
            .Select(pawn => (pawn.GetProperty("seat").GetInt32(), pawn.GetProperty("tile").GetInt32()))
            .ToArray();

    [Theory]
    [InlineData(2)]
    [InlineData(6)]
    public void Every_player_starts_on_the_start_tile_under_the_current_version(int count)
    {
        var (room, _) = Started(count);

        using var state = JsonDocument.Parse(room.State!);
        Assert.Equal(1, state.RootElement.GetProperty("rulesVersion").GetInt32());
        Assert.Equal(Enumerable.Range(1, count).Select(seat => (seat, 0)), Pawns(state.RootElement));
        Assert.Equal(GameDefinition.ChogRace, Rules.GameType);
        Assert.Null(((IMatchRules)Rules).TimeLimit);
    }

    [Fact]
    public void Every_player_sees_the_same_track_and_all_six_pawns()
    {
        var (room, players) = Started(6);

        Assert.Single(players.Select(p => View(room, p.Id).GetRawText()).Distinct());
        var view = View(room, players[0].Id);
        Assert.Equal(1, view.GetProperty("rulesVersion").GetInt32());
        Assert.Equal([(1, 0), (2, 0), (3, 0), (4, 0), (5, 0), (6, 0)], Pawns(view));
    }

    [Fact]
    public void The_track_shows_every_tile_with_its_type_and_jump_target()
    {
        var (room, players) = Started(2);

        var tiles = View(room, players[0].Id).GetProperty("tiles").EnumerateArray().Select(t => t.GetRawText()).ToArray();

        Assert.Equal(28, tiles.Length);
        Assert.Equal("""{"index":0,"type":"Start"}""", tiles[0]);
        Assert.Equal("""{"index":2,"type":"Card"}""", tiles[2]);
        Assert.Equal("""{"index":4,"type":"Boost","to":6}""", tiles[4]);
        Assert.Equal("""{"index":9,"type":"Setback","to":7}""", tiles[9]);
        Assert.Equal("""{"index":27,"type":"Finish"}""", tiles[27]);
    }

    [Fact]
    public void A_player_who_left_is_taken_off_the_track()
    {
        var (room, players) = Started(3);

        room.Leave(players[1].Id, Now.AddMinutes(2));

        Assert.Equal([(1, 0), (3, 0)], Pawns(View(room, players[0].Id)));
        Assert.Equal([(1, 0), (3, 0)], Pawns(View(room, players[1].Id)));
    }

    [Fact]
    public void A_match_keeps_the_track_of_the_version_it_started_with()
    {
        var (room, players) = Started(2);
        var version2 = RaceRules.Create(
            2,
            [Start(0), .. Enumerable.Range(1, 28).Select(Normal), Finish(29)],
            1,
            3,
            TimeSpan.FromMinutes(20),
            [RaceCard.Pump],
            3,
            RaceRulesV1.Rules.CardValues);
        var both = new ChogRaceMatchRules([RaceRulesV1.Rules, version2]);

        Assert.Equal(28, View(room, players[0].Id, both).GetProperty("tiles").GetArrayLength());

        var (newer, newPlayers) = Started(2, both);
        var view = View(newer, newPlayers[0].Id, both);
        Assert.Equal((2, 30), (view.GetProperty("rulesVersion").GetInt32(), view.GetProperty("tiles").GetArrayLength()));
    }

    [Theory]
    [InlineData("""{"rulesVersion":9,"pawns":[]}""")]
    [InlineData("""{"rulesVersion":1}""")]
    [InlineData("""{"rulesVersion":1,"pawns":[null]}""")]
    [InlineData("""{"rulesVersion":"one","pawns":[]}""")]
    [InlineData("""[]""")]
    public void A_state_the_server_cant_read_shows_nothing(string state)
    {
        var (room, players) = Started(2);

        Assert.Null(ViewOf(room, players[0].Id, state));
    }

    private sealed class Dice(params int[] rolls) : IRandomSource
    {
        private readonly Queue<int> _rolls = new(rolls);

        public List<(int Min, int Max)> Draws { get; } = [];

        public int Next(int minInclusive, int maxExclusive)
        {
            Draws.Add((minInclusive, maxExclusive));
            return _rolls.Dequeue();
        }
    }

    /// <summary>A stored state of version 1 with pawns on <paramref name="tiles"/>, by seat from 1.</summary>
    private static string StateWith(int[] tiles, string extra = "") =>
        $$"""{"rulesVersion":1,"pawns":[{{string.Join(",", tiles.Select((tile, i) => $$"""{"seat":{{i + 1}},"tile":{{tile}}}"""))}}]{{extra}}}""";

    private static MatchRuleDecision Apply(
        GameRoom room, Guid player, string type, string state, IRandomSource random, string payload = "{}", ChogRaceMatchRules? rules = null)
    {
        using var stateJson = JsonDocument.Parse(state);
        using var payloadJson = JsonDocument.Parse(payload);
        var decision = (rules ?? Rules).Apply(new MatchCommandContext(room, player, type, payloadJson.RootElement, stateJson.RootElement, random));
        return decision with { State = decision.State?.Clone() };
    }

    private static string LastMove(MatchRuleDecision decision) => decision.State!.Value.GetProperty("lastMove").GetRawText();

    private static int[] Tiles(MatchRuleDecision decision) =>
        decision.State!.Value.GetProperty("pawns").EnumerateArray().Select(pawn => pawn.GetProperty("tile").GetInt32()).ToArray();

    [Fact]
    public void A_roll_moves_the_pawn_by_the_servers_roll_and_ends_the_turn()
    {
        var (room, players) = Started(2);
        var dice = new Dice(3);

        var decision = Apply(room, players[0].Id, ChogRaceMatchRules.Roll, room.State!, dice, """{"roll":6,"to":27}""");

        Assert.Equal((MatchRuleOutcome.Accepted, true, false), (decision.Outcome, decision.EndsTurn, decision.Finished));
        Assert.Equal([(1, 4)], dice.Draws);
        Assert.Equal([3, 0], Tiles(decision));
        Assert.Equal("""{"seat":1,"roll":3,"from":0,"landed":3,"to":3}""", LastMove(decision));
        Assert.False(decision.State!.Value.TryGetProperty("playPhase", out _));
        Assert.Null(decision.Standings);
    }

    [Theory]
    [InlineData(2, 2, 4, 6, "Boost")]
    [InlineData(7, 2, 9, 7, "Setback", ",\"draw\":\"NoCards\"")]
    [InlineData(18, 2, 20, 22, "Boost", ",\"draw\":\"NoCards\"")]
    [InlineData(14, 2, 16, 13, "Setback")]
    [InlineData(0, 2, 2, 2, "Card", ",\"draw\":\"NoCards\"")]
    [InlineData(10, 2, 12, 15, "Boost")]
    public void The_tile_landed_on_resolves(int from, int roll, int landed, int to, string effect, string draw = "")
    {
        var (room, players) = Started(2);
        var dice = new Dice(roll);

        var decision = Apply(room, players[0].Id, ChogRaceMatchRules.Roll, StateWith([from, 0]), dice);

        Assert.True(decision.EndsTurn);
        Assert.Single(dice.Draws);
        Assert.Equal([to, 0], Tiles(decision));
        Assert.Equal($$"""{"seat":1,"roll":{{roll}},"from":{{from}},"landed":{{landed}},"to":{{to}},"effect":"{{effect}}"{{draw}}}""", LastMove(decision));
    }

    [Theory]
    [InlineData(26, 3)]
    [InlineData(25, 2)]
    [InlineData(24, 3)]
    public void Reaching_or_passing_the_finish_wins_and_ranks_the_rest_by_how_far_they_got(int from, int roll)
    {
        var (room, players) = Started(4);

        var decision = Apply(room, players[0].Id, ChogRaceMatchRules.Roll, StateWith([from, 5, 13, 5]), new Dice(roll));

        Assert.Equal(MatchRuleOutcome.Accepted, decision.Outcome);
        Assert.Equal(27, Tiles(decision)[0]);
        Assert.Equal(1, decision.State!.Value.GetProperty("winner").GetInt32());
        Assert.Equal("Finish", decision.State.Value.GetProperty("lastMove").GetProperty("effect").GetString());
        Assert.Equal(
            [(players[0].Id, 1), (players[1].Id, 3), (players[2].Id, 2), (players[3].Id, 3)],
            decision.Standings!.Select(s => (s.PlayerId, s.Rank)));
    }

    [Fact]
    public void Players_who_left_are_not_ranked()
    {
        var (room, players) = Started(3);
        room.Leave(players[2].Id, Now.AddMinutes(2));

        var decision = Apply(room, players[0].Id, ChogRaceMatchRules.Roll, StateWith([26, 3, 20]), new Dice(1));

        Assert.Equal([(players[0].Id, 1), (players[1].Id, 2)], decision.Standings!.Select(s => (s.PlayerId, s.Rank)));
    }

    [Fact]
    public void End_turn_before_the_roll_is_out_of_phase_and_draws_nothing()
    {
        var (room, players) = Started(2);

        var decision = Apply(room, players[0].Id, ChogRaceMatchRules.EndTurn, room.State!, new NoDraws());

        Assert.Equal((MatchRuleOutcome.Rejected, "must_roll_first"), (decision.Outcome, decision.Reason));
        Assert.Null(decision.State);
    }

    [Fact]
    public void In_the_play_phase_a_second_roll_is_out_of_phase_and_end_turn_ends_it()
    {
        var (room, players) = Started(2);
        var playing = StateWith([3, 0], ""","playPhase":1""");

        var rolled = Apply(room, players[0].Id, ChogRaceMatchRules.Roll, playing, new NoDraws());
        var ended = Apply(room, players[0].Id, ChogRaceMatchRules.EndTurn, playing, new NoDraws());

        Assert.Equal((MatchRuleOutcome.Rejected, "already_rolled"), (rolled.Outcome, rolled.Reason));
        Assert.Equal((MatchRuleOutcome.Accepted, true), (ended.Outcome, ended.EndsTurn));
        Assert.Equal(StateWith([3, 0]), ended.State!.Value.GetRawText());
    }

    [Fact]
    public void A_play_phase_left_by_another_seat_doesnt_count_for_the_current_player()
    {
        var (room, players) = Started(2);
        var stale = StateWith([3, 0], ""","playPhase":2""");

        var ended = Apply(room, players[0].Id, ChogRaceMatchRules.EndTurn, stale, new NoDraws());
        var rolled = Apply(room, players[0].Id, ChogRaceMatchRules.Roll, stale, new Dice(1));

        Assert.Equal("must_roll_first", ended.Reason);
        Assert.Equal((MatchRuleOutcome.Accepted, true), (rolled.Outcome, rolled.EndsTurn));
        Assert.False(rolled.State!.Value.TryGetProperty("playPhase", out _));
    }

    [Theory]
    [InlineData("MOVE")]
    [InlineData("CHOOSE_LANE")]
    public void Other_commands_are_unknown(string type)
    {
        var (room, players) = Started(2);

        Assert.Equal(MatchRuleOutcome.UnknownCommand, Apply(room, players[0].Id, type, room.State!, new NoDraws()).Outcome);
    }

    [Theory]
    [InlineData("""{"rulesVersion":9,"pawns":[{"seat":1,"tile":0},{"seat":2,"tile":0}]}""")]
    [InlineData("""{"rulesVersion":1,"pawns":[{"seat":1,"tile":28},{"seat":2,"tile":0}]}""")]
    [InlineData("""{"rulesVersion":1,"pawns":[{"seat":1,"tile":-1},{"seat":2,"tile":0}]}""")]
    [InlineData("""{"rulesVersion":1,"pawns":[{"seat":1,"tile":0},{"seat":1,"tile":3}]}""")]
    [InlineData("""{"rulesVersion":1,"pawns":[{"seat":2,"tile":0}]}""")]
    [InlineData("""{"rulesVersion":1,"pawns":[{"seat":1,"tile":27},{"seat":2,"tile":0}],"winner":1}""")]
    [InlineData("""{"rulesVersion":1,"pawns":[{"seat":1,"tile":0,"hand":["Pump","Pump","Rug","Hodl"]},{"seat":2,"tile":0}]}""")]
    [InlineData("""{"rulesVersion":1,"pawns":[{"seat":1,"tile":0,"hand":[99]},{"seat":2,"tile":0}]}""")]
    [InlineData("""{"rulesVersion":1,"pawns":[{"seat":1,"tile":0,"hand":["Joker"]},{"seat":2,"tile":0}]}""")]
    [InlineData("""{"rulesVersion":1,"pawns":[{"seat":1,"tile":0},{"seat":2,"tile":0}],"deck":["Pump",42]}""")]
    [InlineData("""{"rulesVersion":1,"pawns":[{"seat":1,"tile":0},{"seat":2,"tile":0}],"discard":[-1]}""")]
    [InlineData("""{"rulesVersion":1}""")]
    [InlineData("""[]""")]
    public void A_state_the_server_cant_play_rejects_every_move_and_draws_nothing(string state)
    {
        var (room, players) = Started(2);

        foreach (var type in new[] { ChogRaceMatchRules.Roll, ChogRaceMatchRules.EndTurn, ChogRaceMatchRules.PlayCard })
        {
            var decision = Apply(room, players[0].Id, type, state, new NoDraws());
            Assert.Equal((MatchRuleOutcome.Rejected, "game_state_unavailable"), (decision.Outcome, decision.Reason));
        }
    }

    [Fact]
    public void A_turn_that_runs_out_before_the_roll_is_skipped()
    {
        var (room, _) = Started(2);
        using var state = JsonDocument.Parse(StateWith([3, 0], ""","playPhase":2"""));

        Assert.Null(Rules.TimeOutTurn(new MatchTurnContext(room, JsonDocument.Parse(room.State!).RootElement, new NoDraws())));
        Assert.Null(Rules.TimeOutTurn(new MatchTurnContext(room, state.RootElement, new NoDraws())));
        Assert.Null(Rules.TimeOutTurn(new MatchTurnContext(room, null, new NoDraws())));
    }

    [Fact]
    public void A_turn_that_runs_out_in_the_play_phase_ends_with_no_card_played()
    {
        var (room, _) = Started(2);
        using var state = JsonDocument.Parse(StateWith([3, 0], ""","playPhase":1"""));

        var timedOut = Rules.TimeOutTurn(new MatchTurnContext(room, state.RootElement, new NoDraws()));

        Assert.Equal(StateWith([3, 0]), timedOut!.Value.GetRawText());
    }

    [Fact]
    public void Every_player_sees_the_same_move_and_the_current_players_phase()
    {
        var (room, players) = Started(3);
        var moved = Apply(room, players[0].Id, ChogRaceMatchRules.Roll, room.State!, new Dice(1)).State!.Value.GetRawText();
        var playing = StateWith([3, 0, 0], ""","playPhase":1""");

        var views = players.Skip(1).Select(p => ViewOf(room, p.Id, moved)!.Value.GetRawText()).ToArray();

        Assert.Single(views.Distinct());
        var view = ViewOf(room, players[1].Id, moved)!.Value;
        Assert.Equal("""{"seat":1,"roll":1,"from":0,"landed":1,"to":1}""", view.GetProperty("lastMove").GetRawText());
        Assert.Equal("Move", view.GetProperty("phase").GetString());
        Assert.False(view.TryGetProperty("winner", out _));
        Assert.Equal("Play", ViewOf(room, players[2].Id, playing)!.Value.GetProperty("phase").GetString());
        Assert.Equal("Move", ViewOf(room, players[2].Id, StateWith([3, 0, 0], ""","playPhase":2"""))!.Value.GetProperty("phase").GetString());
    }

    [Fact]
    public void The_rules_need_at_least_one_version()
    {
        Assert.Throws<ArgumentException>(() => new ChogRaceMatchRules([]));
        Assert.Throws<ArgumentNullException>(() => new ChogRaceMatchRules(null!));
    }

    /// <summary>
    /// A stored state of version 1 with pawns on <paramref name="tiles"/> holding <paramref name="hands"/>, by seat from 1,
    /// and the deck, top first, and discard pile.
    /// </summary>
    private static string CardState(int[] tiles, string[][] hands, string[] deck, string[] discard)
    {
        static string Cards(string[] cards) => $"[{string.Join(",", cards.Select(card => $"\"{card}\""))}]";
        var pawns = tiles.Select((tile, i) =>
            $$"""{"seat":{{i + 1}},"tile":{{tile}}{{(hands[i].Length == 0 ? "" : $",\"hand\":{Cards(hands[i])}")}}}""");
        return $$"""{"rulesVersion":1,"pawns":[{{string.Join(",", pawns)}}],"deck":{{Cards(deck)}},"discard":{{Cards(discard)}}}""";
    }

    private static string[] Cards(JsonElement? cards) =>
        cards is { ValueKind: JsonValueKind.Array } array ? array.EnumerateArray().Select(card => card.GetString()!).ToArray() : [];

    private static string[] HandOf(MatchRuleDecision decision, int seat) =>
        Cards(decision.State!.Value.GetProperty("pawns").EnumerateArray()
            .Single(pawn => pawn.GetProperty("seat").GetInt32() == seat)
            .TryGetProperty("hand", out var hand) ? hand : null);

    private static string[] Pile(MatchRuleDecision decision, string pile) =>
        Cards(decision.State!.Value.TryGetProperty(pile, out var cards) ? cards : null);

    [Fact]
    public void The_match_starts_with_the_whole_deck_shuffled_by_the_server_and_empty_hands()
    {
        var (room, _) = Started(2);
        var random = new Dice([.. Enumerable.Repeat(0, 23)]);

        var start = Rules.StartState(new MatchStartContext(room, random))!.Value;

        // Fisher–Yates over 24 cards: one draw per position from the last, each here swapping it with the first.
        Assert.Equal(Enumerable.Range(1, 23).Reverse().Select(i => (0, i + 1)), random.Draws);
        var shuffled = RaceRulesV1.Rules.Deck.Select(card => card.ToString()).ToArray();
        for (var i = shuffled.Length - 1; i > 0; i--)
        {
            (shuffled[i], shuffled[0]) = (shuffled[0], shuffled[i]);
        }

        Assert.Equal(shuffled, Cards(start.GetProperty("deck")));
        Assert.Empty(Cards(start.GetProperty("discard")));
        Assert.All(start.GetProperty("pawns").EnumerateArray(), pawn => Assert.False(pawn.TryGetProperty("hand", out _)));
    }

    [Fact]
    public void Every_player_sees_the_deck_size_but_never_its_order()
    {
        var (room, players) = Started(2);

        var view = View(room, players[0].Id);

        Assert.Equal(
            (24, 0, 3),
            (view.GetProperty("deck").GetInt32(), view.GetProperty("discard").GetInt32(), view.GetProperty("maxHand").GetInt32()));
        Assert.Empty(Cards(view.GetProperty("hand")));
        Assert.Equal([0, 0], view.GetProperty("pawns").EnumerateArray().Select(pawn => pawn.GetProperty("cards").GetInt32()));
        Assert.DoesNotContain("Pump", view.GetRawText());
    }

    [Fact]
    public void Landing_on_a_card_tile_draws_the_top_card_and_still_ends_the_turn()
    {
        var (room, players) = Started(2);
        var dice = new Dice(2);

        var decision = Apply(
            room, players[0].Id, ChogRaceMatchRules.Roll, CardState([0, 0], [["Hodl"], []], ["Hodl", "Whale"], ["Fomo"]), dice);

        Assert.Equal((MatchRuleOutcome.Accepted, true), (decision.Outcome, decision.EndsTurn));
        Assert.Single(dice.Draws);
        Assert.Equal(["Hodl", "Hodl"], HandOf(decision, 1));
        Assert.Empty(HandOf(decision, 2));
        Assert.Equal(["Whale"], Pile(decision, "deck"));
        Assert.Equal(["Fomo"], Pile(decision, "discard"));
        Assert.False(decision.State!.Value.TryGetProperty("playPhase", out _));
        Assert.Equal(
            """{"seat":1,"roll":2,"from":0,"landed":2,"to":2,"effect":"Card","draw":"Drawn","card":"Hodl"}""",
            LastMove(decision));
    }

    [Fact]
    public void A_full_hand_draws_nothing_and_leaves_the_deck_alone()
    {
        var (room, players) = Started(2);
        var state = CardState([0, 0], [["Hodl", "Hodl", "Hodl"], []], ["Pump"], []);

        var decision = Apply(room, players[0].Id, ChogRaceMatchRules.Roll, state, new Dice(2));

        Assert.True(decision.EndsTurn);
        Assert.Equal(["Hodl", "Hodl", "Hodl"], HandOf(decision, 1));
        Assert.Equal(["Pump"], Pile(decision, "deck"));
        Assert.Equal("""{"seat":1,"roll":2,"from":0,"landed":2,"to":2,"effect":"Card","draw":"HandFull"}""", LastMove(decision));
    }

    [Fact]
    public void An_empty_deck_is_refilled_from_the_discard_pile_and_the_hands_of_players_who_left()
    {
        var (room, players) = Started(3);
        room.Leave(players[2].Id, Now.AddMinutes(2));
        var state = CardState([0, 0, 5], [[], ["Hodl"], ["Whale"]], [], ["Pump", "Rug"]);
        // The roll of 2, then the shuffle of [Pump, Rug, Whale]: position 2 swaps with 0, position 1 stays.
        var dice = new Dice(2, 0, 1);

        var decision = Apply(room, players[0].Id, ChogRaceMatchRules.Roll, state, dice);

        Assert.Equal([(1, 4), (0, 3), (0, 2)], dice.Draws);
        Assert.Equal(["Whale"], HandOf(decision, 1));
        Assert.Equal(["Hodl"], HandOf(decision, 2));
        Assert.Empty(HandOf(decision, 3));
        Assert.Equal(["Rug", "Pump"], Pile(decision, "deck"));
        Assert.Empty(Pile(decision, "discard"));
        Assert.Equal(
            """{"seat":1,"roll":2,"from":0,"landed":2,"to":2,"effect":"Card","draw":"Drawn","card":"Whale","reshuffled":true}""",
            LastMove(decision));
    }

    [Fact]
    public void With_every_card_in_a_hand_nothing_is_drawn()
    {
        var (room, players) = Started(2);
        var dice = new Dice(2);

        var decision = Apply(room, players[0].Id, ChogRaceMatchRules.Roll, CardState([0, 0], [[], ["Pump"]], [], []), dice);

        Assert.Equal((MatchRuleOutcome.Accepted, true), (decision.Outcome, decision.EndsTurn));
        Assert.Single(dice.Draws);
        Assert.Empty(HandOf(decision, 1));
        Assert.Equal(["Pump"], HandOf(decision, 2));
        Assert.Equal("NoCards", decision.State!.Value.GetProperty("lastMove").GetProperty("draw").GetString());
    }

    [Fact]
    public void A_hand_is_shown_only_to_its_owner_and_the_others_see_how_many_cards_it_holds()
    {
        var (room, players) = Started(3);
        var drawn = Apply(
            room, players[0].Id, ChogRaceMatchRules.Roll, CardState([0, 0, 0], [["Rug"], ["Hodl", "Fomo"], []], ["Pump"], []), new Dice(2));
        var state = drawn.State!.Value.GetRawText();

        var own = ViewOf(room, players[0].Id, state)!.Value;
        var other = ViewOf(room, players[1].Id, state)!.Value;

        Assert.Equal(["Rug", "Pump"], Cards(own.GetProperty("hand")));
        Assert.Equal("Pump", own.GetProperty("lastMove").GetProperty("card").GetString());
        Assert.Equal(["Hodl", "Fomo"], Cards(other.GetProperty("hand")));
        Assert.False(other.GetProperty("lastMove").TryGetProperty("card", out _));
        Assert.DoesNotContain("Rug", other.GetRawText());
        Assert.DoesNotContain("Pump", other.GetRawText());
        Assert.Equal([2, 2, 0], other.GetProperty("pawns").EnumerateArray().Select(pawn => pawn.GetProperty("cards").GetInt32()));
        Assert.Equal((0, 0), (other.GetProperty("deck").GetInt32(), other.GetProperty("discard").GetInt32()));
    }

    [Fact]
    public void A_player_who_left_sees_no_hand()
    {
        var (room, players) = Started(3);
        room.Leave(players[2].Id, Now.AddMinutes(2));
        var state = CardState([0, 0, 0], [[], [], ["Pump"]], [], []);

        var view = ViewOf(room, players[2].Id, state)!.Value;

        Assert.False(view.TryGetProperty("hand", out _));
        Assert.DoesNotContain("Pump", view.GetRawText());
    }

    [Fact]
    public void A_card_played_only_in_reply_doesnt_open_the_play_phase()
    {
        var (room, players) = Started(2);

        var decision = Apply(
            room, players[0].Id, ChogRaceMatchRules.Roll, CardState([0, 0], [["Hodl", "Hodl"], []], ["Whale"], []), new Dice(1));

        Assert.True(decision.EndsTurn);
        Assert.False(decision.State!.Value.TryGetProperty("playPhase", out _));
    }

    [Theory]
    [InlineData("Pump")]
    [InlineData("Whale")]
    [InlineData("Airdrop")]
    [InlineData("Fomo")]
    [InlineData("Reverse")]
    [InlineData("Rug")]
    [InlineData("Liquidated")]
    public void Holding_a_card_playable_on_the_turn_opens_the_play_phase(string card)
    {
        var (room, players) = Started(2);

        var decision = Apply(
            room, players[0].Id, ChogRaceMatchRules.Roll, CardState([0, 0], [["Hodl", card], []], ["Rug"], []), new Dice(1));

        Assert.Equal((MatchRuleOutcome.Accepted, false), (decision.Outcome, decision.EndsTurn));
        Assert.Equal(1, decision.State!.Value.GetProperty("playPhase").GetInt32());
    }

    [Fact]
    public void A_card_drawn_on_the_roll_can_be_played_the_same_turn()
    {
        var (room, players) = Started(2);

        var decision = Apply(room, players[0].Id, ChogRaceMatchRules.Roll, CardState([0, 0], [[], []], ["Pump"], []), new Dice(2));

        Assert.False(decision.EndsTurn);
        Assert.Equal(1, decision.State!.Value.GetProperty("playPhase").GetInt32());
        Assert.Equal(["Pump"], HandOf(decision, 1));
    }

    /// <summary>
    /// <see cref="CardState"/> in seat 1's Play phase, after a roll of 1 from tile 0; seat 1 sends <c>PLAY_CARD</c> next.
    /// </summary>
    private static string PlayState(int[] tiles, string[][] hands, string[]? deck = null, string[]? discard = null)
    {
        var state = CardState(tiles, hands, deck ?? [], discard ?? []);
        return state[..^1] + ""","lastMove":{"seat":1,"roll":1,"from":0,"landed":1,"to":1},"playPhase":1}""";
    }

    private static MatchRuleDecision Play(GameRoom room, Guid player, string state, IRandomSource random, string payload) =>
        Apply(room, player, ChogRaceMatchRules.PlayCard, state, random, payload);

    private static string PlayOf(MatchRuleDecision decision) =>
        decision.State!.Value.GetProperty("lastMove").GetProperty("play").GetRawText();

    [Fact]
    public void Pump_moves_the_player_forward_discards_the_card_and_ends_the_turn()
    {
        var (room, players) = Started(2);

        var decision = Play(
            room, players[0].Id, PlayState([5, 0], [["Pump", "Pump", "Rug"], []], discard: ["Fomo"]), new NoDraws(), """{"card":"Pump"}""");

        Assert.Equal((MatchRuleOutcome.Accepted, true, false), (decision.Outcome, decision.EndsTurn, decision.Finished));
        Assert.Equal([8, 0], Tiles(decision));
        Assert.Equal(["Pump", "Rug"], HandOf(decision, 1));
        Assert.Equal(["Fomo", "Pump"], Pile(decision, "discard"));
        Assert.False(decision.State!.Value.TryGetProperty("playPhase", out _));
        Assert.Equal(
            """{"seat":1,"roll":1,"from":0,"landed":1,"to":1,"play":{"card":"Pump","steps":3,"from":5,"landed":8,"to":8}}""",
            LastMove(decision));
    }

    [Fact]
    public void A_card_move_resolves_the_tile_its_player_lands_on()
    {
        var (room, players) = Started(2);

        var boosted = Play(room, players[0].Id, PlayState([1, 0], [["Pump"], []]), new NoDraws(), """{"card":"Pump"}""");
        var drew = Play(room, players[0].Id, PlayState([4, 0], [["Pump"], []], deck: ["Rug", "Hodl"]), new NoDraws(), """{"card":"Pump"}""");

        Assert.Equal([6, 0], Tiles(boosted));
        Assert.Equal("""{"card":"Pump","steps":3,"from":1,"landed":4,"to":6,"effect":"Boost"}""", PlayOf(boosted));
        Assert.Empty(HandOf(boosted, 1));
        Assert.Equal(["Rug"], HandOf(drew, 1));
        Assert.Equal(["Hodl"], Pile(drew, "deck"));
        Assert.Equal("""{"card":"Pump","steps":3,"from":4,"landed":7,"to":7,"effect":"Card","draw":"Drawn","drawn":"Rug"}""", PlayOf(drew));
    }

    [Fact]
    public void A_card_drawn_by_a_card_move_is_shown_only_to_its_player_and_the_card_played_to_everyone()
    {
        var (room, players) = Started(3);
        var state = Play(room, players[0].Id, PlayState([4, 0, 0], [["Pump", "Fomo"], [], []], deck: ["Rug"]), new NoDraws(), """{"card":"Pump"}""")
            .State!.Value.GetRawText();

        var own = ViewOf(room, players[0].Id, state)!.Value;
        var other = ViewOf(room, players[1].Id, state)!.Value;

        Assert.Equal("Rug", own.GetProperty("lastMove").GetProperty("play").GetProperty("drawn").GetString());
        Assert.Equal("""{"card":"Pump","steps":3,"from":4,"landed":7,"to":7,"effect":"Card","draw":"Drawn"}""", other.GetProperty("lastMove").GetProperty("play").GetRawText());
        Assert.DoesNotContain("Rug", other.GetRawText());
        Assert.DoesNotContain("Fomo", other.GetRawText());
        Assert.Equal(1, other.GetProperty("discard").GetInt32());
    }

    [Fact]
    public void A_card_move_that_reaches_the_finish_wins()
    {
        var (room, players) = Started(3);

        var decision = Play(room, players[0].Id, PlayState([26, 10, 12], [["Pump"], [], []]), new NoDraws(), """{"card":"Pump"}""");

        Assert.Equal(MatchRuleOutcome.Accepted, decision.Outcome);
        Assert.Equal(27, Tiles(decision)[0]);
        Assert.Equal(1, decision.State!.Value.GetProperty("winner").GetInt32());
        Assert.Equal("""{"card":"Pump","steps":3,"from":26,"landed":27,"to":27,"effect":"Finish"}""", PlayOf(decision));
        Assert.Equal([(players[0].Id, 1), (players[1].Id, 3), (players[2].Id, 2)], decision.Standings!.Select(s => (s.PlayerId, s.Rank)));
    }

    [Theory]
    [InlineData(0, 8, 13, 5)]
    [InlineData(49, 8, 13, 5)]
    [InlineData(50, 8, 5, -3)]
    [InlineData(99, 8, 5, -3)]
    [InlineData(99, 1, 0, -3)]
    public void Fomo_goes_forward_or_back_on_one_draw_and_stops_at_the_start(int draw, int from, int to, int steps)
    {
        var (room, players) = Started(2);
        var dice = new Dice(draw);

        var decision = Play(room, players[0].Id, PlayState([from, 0], [["Fomo"], []]), dice, """{"card":"Fomo"}""");

        Assert.Equal([(0, 100)], dice.Draws);
        Assert.Equal([to, 0], Tiles(decision));
        Assert.Equal($$"""{"card":"Fomo","steps":{{steps}},"from":{{from}},"landed":{{to}},"to":{{to}}}""", PlayOf(decision));
    }

    [Fact]
    public void Airdrop_rolls_again_on_the_server_and_resolves_the_tile_like_a_roll()
    {
        var (room, players) = Started(2);
        var dice = new Dice(3);

        var decision = Play(room, players[0].Id, PlayState([1, 0], [["Airdrop"], []]), dice, """{"card":"Airdrop","roll":3}""");

        Assert.Equal([(1, 4)], dice.Draws);
        Assert.Equal([6, 0], Tiles(decision));
        Assert.True(decision.EndsTurn);
        Assert.Equal("""{"card":"Airdrop","roll":3,"from":1,"landed":4,"to":6,"effect":"Boost"}""", PlayOf(decision));
    }

    [Fact]
    public void A_boost_or_setback_onto_a_card_tile_draws_a_card()
    {
        var (room, players) = Started(2);

        var decision = Apply(room, players[0].Id, ChogRaceMatchRules.Roll, CardState([7, 0], [[], []], ["Rug"], []), new Dice(2));

        Assert.Equal([7, 0], Tiles(decision));
        Assert.Equal(["Rug"], HandOf(decision, 1));
        Assert.Equal(
            """{"seat":1,"roll":2,"from":7,"landed":9,"to":7,"effect":"Setback","draw":"Drawn","card":"Rug"}""",
            LastMove(decision));
    }

    [Fact]
    public void Whale_swaps_tiles_with_the_chosen_opponent()
    {
        var (room, players) = Started(3);

        var decision = Play(room, players[0].Id, PlayState([5, 8, 0], [["Whale"], [], []]), new NoDraws(), """{"card":"Whale","target":2}""");

        Assert.Equal((MatchRuleOutcome.Accepted, true), (decision.Outcome, decision.EndsTurn));
        Assert.Equal([8, 5, 0], Tiles(decision));
        Assert.Equal("""{"card":"Whale","from":5,"to":8,"target":2}""", PlayOf(decision));
    }

    [Fact]
    public void Whale_onto_a_card_tile_draws_the_player_a_card_but_not_the_target()
    {
        var (room, players) = Started(2);

        var decision = Play(room, players[0].Id, PlayState([11, 14], [["Whale"], []], deck: ["Rug", "Hodl"]), new NoDraws(), """{"card":"Whale","target":2}""");

        Assert.Equal([14, 11], Tiles(decision));
        Assert.Equal(["Rug"], HandOf(decision, 1));
        Assert.Empty(HandOf(decision, 2));
        Assert.Equal(["Hodl"], Pile(decision, "deck"));
        Assert.Equal("""{"card":"Whale","from":11,"to":14,"effect":"Card","draw":"Drawn","drawn":"Rug","target":2}""", PlayOf(decision));
    }

    [Theory]
    [InlineData("""{"card":"Whale"}""")]
    [InlineData("""{"card":"Whale","target":1}""")]
    [InlineData("""{"card":"Whale","target":3}""")]
    [InlineData("""{"card":"Whale","target":7}""")]
    [InlineData("""{"card":"Whale","target":"2"}""")]
    [InlineData("""{"card":"Whale","target":2.5}""")]
    public void Whale_needs_an_opponent_still_playing_as_its_target(string payload)
    {
        var (room, players) = Started(3);
        room.Leave(players[2].Id, Now.AddMinutes(2));

        var decision = Play(room, players[0].Id, PlayState([5, 9, 3], [["Whale"], [], []]), new NoDraws(), payload);

        Assert.Equal((MatchRuleOutcome.Rejected, "invalid_target"), (decision.Outcome, decision.Reason));
        Assert.Null(decision.State);
    }

    [Fact]
    public void Reverse_moves_every_opponent_still_playing_back_and_a_boost_or_setback_sends_them_on()
    {
        var (room, players) = Started(5);
        room.Leave(players[4].Id, Now.AddMinutes(2));

        var decision = Play(
            room, players[0].Id, PlayState([10, 0, 5, 24, 8], [["Reverse"], [], [], [], []], deck: ["Rug"]), new NoDraws(), """{"card":"Reverse"}""");

        // Seat 2 is on the start already; seat 3 lands on the Boost at 4 and jumps to 6; seat 4 on the Setback at 23 and
        // falls back to 21; seat 5 left. Nobody draws on another player's turn.
        Assert.Equal([10, 0, 6, 21, 8], Tiles(decision));
        Assert.Equal(
            """{"card":"Reverse","pushed":[{"seat":3,"from":5,"landed":4,"to":6,"effect":"Boost"},{"seat":4,"from":24,"landed":23,"to":21,"effect":"Setback"}]}""",
            PlayOf(decision));
        Assert.Equal(["Rug"], Pile(decision, "deck"));
    }

    [Fact]
    public void A_pawn_reverse_pushes_onto_a_card_tile_draws_nothing()
    {
        var (room, players) = Started(2);

        var decision = Play(room, players[0].Id, PlayState([10, 8], [["Reverse"], []], deck: ["Rug"]), new NoDraws(), """{"card":"Reverse"}""");

        Assert.Equal([10, 7], Tiles(decision));
        Assert.Equal("""{"card":"Reverse","pushed":[{"seat":2,"from":8,"landed":7,"to":7}]}""", PlayOf(decision));
        Assert.Empty(HandOf(decision, 2));
        Assert.Equal(["Rug"], Pile(decision, "deck"));
    }

    [Fact]
    public void A_pawn_reverse_pushes_onto_a_boost_to_the_finish_wins()
    {
        var boostToFinish = RaceRules.Create(
            2,
            [Start(0), .. Enumerable.Range(1, 25).Select(Normal), Boost(26, to: 29), Normal(27), Normal(28), Finish(29)],
            1,
            3,
            TimeSpan.FromMinutes(20),
            [RaceCard.Reverse],
            3,
            RaceRulesV1.Rules.CardValues);
        var rules = new ChogRaceMatchRules([RaceRulesV1.Rules, boostToFinish]);
        var (room, players) = Started(2, rules);
        var state = PlayState([10, 27], [["Reverse"], []]).Replace("\"rulesVersion\":1", "\"rulesVersion\":2");

        var decision = Apply(room, players[0].Id, ChogRaceMatchRules.PlayCard, state, new NoDraws(), """{"card":"Reverse"}""", rules);

        Assert.Equal(2, decision.State!.Value.GetProperty("winner").GetInt32());
        Assert.Equal([(players[0].Id, 2), (players[1].Id, 1)], decision.Standings!.Select(s => (s.PlayerId, s.Rank)));
    }

    [Theory]
    [InlineData("{}", "invalid_card")]
    [InlineData("""{"card":"pump"}""", "invalid_card")]
    [InlineData("""{"card":"0"}""", "invalid_card")]
    [InlineData("""{"card":0}""", "invalid_card")]
    [InlineData("""{"card":"Joker"}""", "invalid_card")]
    [InlineData("""[]""", "invalid_card")]
    [InlineData("""{"card":"Fomo"}""", "card_not_held")]
    [InlineData("""{"card":"Rug"}""", "invalid_target")]
    [InlineData("""{"card":"Hodl"}""", "card_not_playable")]
    [InlineData("""{"card":"Hodl","target":2}""", "card_not_playable")]
    public void Only_a_held_card_playable_on_the_turn_can_be_played(string payload, string reason)
    {
        var (room, players) = Started(2);

        var decision = Play(room, players[0].Id, PlayState([5, 0], [["Pump", "Rug", "Hodl"], ["Fomo"]]), new NoDraws(), payload);

        Assert.Equal((MatchRuleOutcome.Rejected, reason), (decision.Outcome, decision.Reason));
        Assert.Null(decision.State);
    }

    [Fact]
    public void A_card_can_only_be_played_in_the_players_own_play_phase()
    {
        var (room, players) = Started(2);
        var beforeRoll = CardState([5, 0], [["Pump"], []], [], []);
        var othersPhase = PlayState([5, 0], [["Pump"], []]).Replace("\"playPhase\":1", "\"playPhase\":2");
        var noMove = beforeRoll[..^1] + ",\"playPhase\":1}";

        Assert.Equal("must_roll_first", Play(room, players[0].Id, beforeRoll, new NoDraws(), """{"card":"Pump"}""").Reason);
        Assert.Equal("must_roll_first", Play(room, players[0].Id, othersPhase, new NoDraws(), """{"card":"Pump"}""").Reason);
        Assert.Equal("game_state_unavailable", Play(room, players[0].Id, noMove, new NoDraws(), """{"card":"Pump"}""").Reason);
    }

    [Fact]
    public void A_player_sees_which_of_their_cards_they_can_play_and_the_card_values()
    {
        var (room, players) = Started(2);
        var state = PlayState([5, 0], [["Pump", "Rug", "Pump"], ["Fomo"]]);

        var own = ViewOf(room, players[0].Id, state)!.Value;
        var other = ViewOf(room, players[1].Id, state)!.Value;

        Assert.Equal(["Pump", "Rug"], Cards(own.GetProperty("playable")));
        Assert.Equal(["Fomo"], Cards(other.GetProperty("playable")));
        Assert.Equal(
            """{"pump":3,"fomoForward":5,"fomoBack":3,"fomoForwardChance":50,"reverse":1,"rug":4}""",
            own.GetProperty("cardValues").GetRawText());
        Assert.Equal("Play", own.GetProperty("phase").GetString());
    }

    [Fact]
    public void Rug_on_a_player_without_cards_moves_them_back_at_once_and_ends_the_turn()
    {
        var (room, players) = Started(2);

        var decision = Play(room, players[0].Id, PlayState([5, 10], [["Rug"], []], deck: ["Pump"]), new NoDraws(), """{"card":"Rug","target":2}""");

        Assert.Equal((MatchRuleOutcome.Accepted, true, (Guid?)null), (decision.Outcome, decision.EndsTurn, decision.AwaitsReplyFrom));
        Assert.Equal([5, 6], Tiles(decision));
        Assert.Equal("""{"card":"Rug","target":2,"pushed":[{"seat":2,"from":10,"landed":6,"to":6}]}""", PlayOf(decision));
        Assert.Equal(["Rug"], Pile(decision, "discard"));
        Assert.Empty(HandOf(decision, 1));
    }

    [Theory]
    [InlineData(13, 9, 7, "Setback")] // onto the Setback at 9, back to 7, a Card tile that draws nothing
    [InlineData(8, 4, 6, "Boost")] // onto the Boost at 4, on to 6
    [InlineData(3, 0, 0, null)] // no further back than the Start
    public void Rug_stops_at_the_start_and_follows_the_boost_or_setback_it_lands_on(int from, int landed, int to, string? effect)
    {
        var (room, players) = Started(2);

        var decision = Play(room, players[0].Id, PlayState([5, from], [["Rug"], []], deck: ["Pump"]), new NoDraws(), """{"card":"Rug","target":2}""");

        Assert.Equal([5, to], Tiles(decision));
        var push = $$"""{"seat":2,"from":{{from}},"landed":{{landed}},"to":{{to}}""" + (effect is null ? "}" : $$""","effect":"{{effect}}"}""");
        Assert.Equal($$"""{"card":"Rug","target":2,"pushed":[{{push}}]}""", PlayOf(decision));
        Assert.Empty(HandOf(decision, 2));
        Assert.Equal(["Pump"], Pile(decision, "deck"));
    }

    [Fact]
    public void Liquidated_on_a_player_without_cards_makes_them_skip_their_next_turn()
    {
        var (room, players) = Started(3);

        var decision = Play(room, players[0].Id, PlayState([5, 10, 2], [["Liquidated"], [], []]), new NoDraws(), """{"card":"Liquidated","target":3}""");

        Assert.Equal((MatchRuleOutcome.Accepted, true, players[2].Id), (decision.Outcome, decision.EndsTurn, decision.SkipsNextTurnOf));
        Assert.Equal([5, 10, 2], Tiles(decision));
        Assert.Equal("""{"card":"Liquidated","target":3}""", PlayOf(decision));
        Assert.Equal(["Liquidated"], Pile(decision, "discard"));
    }

    [Theory]
    [InlineData("""{"card":"Rug"}""")]
    [InlineData("""{"card":"Rug","target":1}""")]
    [InlineData("""{"card":"Rug","target":3}""")]
    [InlineData("""{"card":"Rug","target":7}""")]
    [InlineData("""{"card":"Rug","target":"2"}""")]
    [InlineData("""{"card":"Rug","target":4}""")] // on the Start: nowhere to go back to
    [InlineData("""{"card":"Liquidated"}""")]
    [InlineData("""{"card":"Liquidated","target":1}""")]
    [InlineData("""{"card":"Liquidated","target":3}""")]
    [InlineData("""{"card":"Liquidated","target":5}""")] // already due to skip a turn; it doesn't stack
    public void An_attack_needs_an_opponent_still_playing_it_can_hurt_as_its_target(string payload)
    {
        var (room, players) = Started(5);
        room.Leave(players[2].Id, Now.AddMinutes(1));
        Skips(room, players[0], players[4]);

        var decision = Play(room, players[0].Id, PlayState([5, 9, 3, 0, 6], [["Rug", "Liquidated"], [], [], [], []]), new NoDraws(), payload);

        Assert.Equal((MatchRuleOutcome.Rejected, "invalid_target"), (decision.Outcome, decision.Reason));
        Assert.Null(decision.State);
    }

    /// <summary>Makes <paramref name="skipping"/> due to skip their next turn, on <paramref name="current"/>'s turn.</summary>
    private static void Skips(GameRoom room, Player current, Player skipping) =>
        room.ApplyCommand(current.Id, Command(room), "{}", endsTurn: false, Now.AddMinutes(1), skipsNextTurnOf: skipping.Id);

    private static MatchCommand Command(GameRoom room)
    {
        using var payload = JsonDocument.Parse("{}");
        return new MatchCommand(Guid.NewGuid(), room.Id, room.Version, ChogRaceMatchRules.PlayCard, payload.RootElement.Clone());
    }

    [Theory]
    [InlineData("Rug")]
    [InlineData("Liquidated")]
    [InlineData("Whale")]
    public void An_attack_on_a_player_holding_cards_waits_for_their_reply(string card)
    {
        var (room, players) = Started(3);

        var decision = Play(
            room, players[0].Id, PlayState([5, 10, 2], [[card], ["Pump"], []]), new NoDraws(), $$"""{"card":"{{card}}","target":2}""");

        Assert.Equal((MatchRuleOutcome.Accepted, false, players[1].Id), (decision.Outcome, decision.EndsTurn, decision.AwaitsReplyFrom));
        Assert.Null(decision.SkipsNextTurnOf);
        Assert.Equal([5, 10, 2], Tiles(decision));
        Assert.Equal($$"""{"seat":1,"card":"{{card}}","target":2}""", decision.State!.Value.GetProperty("reply").GetRawText());
        Assert.Equal($$"""{"card":"{{card}}","target":2}""", PlayOf(decision));
        Assert.Equal([card], Pile(decision, "discard"));
        Assert.False(decision.State!.Value.TryGetProperty("playPhase", out _));
    }

    /// <summary>
    /// Seat 1's <paramref name="card"/> on seat 2 waiting for seat 2's reply, the engine waiting with it; the card is on
    /// the discard pile.
    /// </summary>
    private static string ReplyState(GameRoom room, Player[] players, string card, int[] tiles, string[][] hands, string[]? deck = null)
    {
        room.ApplyCommand(players[0].Id, Command(room), "{}", endsTurn: false, Now.AddMinutes(1), awaitsReplyFrom: players[1].Id);
        var state = CardState(tiles, hands, deck ?? [], [card]);
        return state[..^1]
            + $$$""","lastMove":{"seat":1,"roll":1,"from":0,"landed":1,"to":1,"play":{"card":"{{{card}}}","target":2}},"reply":{"seat":1,"card":"{{{card}}}","target":2}}""";
    }

    [Theory]
    [InlineData("Rug")]
    [InlineData("Liquidated")]
    [InlineData("Whale")]
    public void Hodl_blocks_the_attack_and_is_used_up_once(string card)
    {
        var (room, players) = Started(2);
        var state = ReplyState(room, players, card, [5, 10], [[], ["Hodl", "Hodl"]]);

        var decision = Play(room, players[1].Id, state, new NoDraws(), """{"card":"Hodl"}""");

        Assert.Equal((MatchRuleOutcome.Accepted, true), (decision.Outcome, decision.EndsTurn));
        Assert.Equal(((Guid?)null, (Guid?)null), (decision.AwaitsReplyFrom, decision.SkipsNextTurnOf));
        Assert.Equal([5, 10], Tiles(decision));
        Assert.Equal(["Hodl"], HandOf(decision, 2));
        Assert.Equal([card, "Hodl"], Pile(decision, "discard"));
        Assert.Equal($$"""{"card":"{{card}}","target":2,"reply":"Blocked"}""", PlayOf(decision));
        Assert.False(decision.State!.Value.TryGetProperty("reply", out _));
    }

    [Fact]
    public void Pass_lets_a_rug_land_and_keeps_the_targets_cards()
    {
        var (room, players) = Started(2);
        var state = ReplyState(room, players, "Rug", [5, 10], [[], ["Hodl"]]);

        var decision = Apply(room, players[1].Id, ChogRaceMatchRules.Pass, state, new NoDraws());

        Assert.Equal((MatchRuleOutcome.Accepted, true), (decision.Outcome, decision.EndsTurn));
        Assert.Equal([5, 6], Tiles(decision));
        Assert.Equal(["Hodl"], HandOf(decision, 2));
        Assert.Equal(
            """{"card":"Rug","target":2,"pushed":[{"seat":2,"from":10,"landed":6,"to":6}],"reply":"Allowed"}""", PlayOf(decision));
        Assert.False(decision.State!.Value.TryGetProperty("reply", out _));
    }

    [Fact]
    public void Pass_lets_liquidated_skip_the_targets_next_turn()
    {
        var (room, players) = Started(2);
        var state = ReplyState(room, players, "Liquidated", [5, 10], [[], ["Pump"]]);

        var decision = Apply(room, players[1].Id, ChogRaceMatchRules.Pass, state, new NoDraws());

        Assert.Equal((true, players[1].Id), (decision.EndsTurn, decision.SkipsNextTurnOf));
        Assert.Equal("""{"card":"Liquidated","target":2,"reply":"Allowed"}""", PlayOf(decision));
    }

    [Fact]
    public void Pass_lets_a_whale_swap_and_the_attacker_draws_on_a_card_tile()
    {
        var (room, players) = Started(2);
        var state = ReplyState(room, players, "Whale", [5, 14], [[], ["Hodl"]], deck: ["Pump"]);

        var decision = Apply(room, players[1].Id, ChogRaceMatchRules.Pass, state, new NoDraws());

        Assert.Equal([14, 5], Tiles(decision));
        Assert.Equal(["Pump"], HandOf(decision, 1));
        Assert.Equal(
            """{"card":"Whale","from":5,"to":14,"effect":"Card","draw":"Drawn","drawn":"Pump","target":2,"reply":"Allowed"}""",
            PlayOf(decision));
    }

    [Theory]
    [InlineData(ChogRaceMatchRules.Roll, "{}", "reply_pending")]
    [InlineData(ChogRaceMatchRules.EndTurn, "{}", "reply_pending")]
    [InlineData(ChogRaceMatchRules.PlayCard, """{"card":"Pump"}""", "card_not_playable")]
    [InlineData(ChogRaceMatchRules.PlayCard, """{"card":"Joker"}""", "invalid_card")]
    public void While_an_attack_waits_its_target_can_only_play_hodl_or_pass(string type, string payload, string reason)
    {
        var (room, players) = Started(2);
        var state = ReplyState(room, players, "Rug", [5, 10], [[], ["Pump"]]);

        var decision = Apply(room, players[1].Id, type, state, new NoDraws(), payload);

        Assert.Equal((MatchRuleOutcome.Rejected, reason), (decision.Outcome, decision.Reason));
        Assert.Equal("card_not_held", Play(room, players[1].Id, state, new NoDraws(), """{"card":"Hodl"}""").Reason);
        Assert.Equal("reply_pending", Apply(room, players[0].Id, ChogRaceMatchRules.Roll, state, new NoDraws()).Reason);
    }

    [Fact]
    public void Pass_without_an_attack_waiting_is_rejected()
    {
        var (room, players) = Started(2);

        var decision = Apply(room, players[0].Id, ChogRaceMatchRules.Pass, PlayState([5, 10], [["Rug"], []]), new NoDraws());

        Assert.Equal((MatchRuleOutcome.Rejected, "no_attack"), (decision.Outcome, decision.Reason));
    }

    [Fact]
    public void An_attack_the_engine_stopped_waiting_for_is_void()
    {
        // The target left, so the engine moved on; the stored attack no longer holds anyone up.
        var (room, players) = Started(3);
        var state = ReplyState(room, players, "Rug", [5, 10, 0], [[], ["Pump"], []]);
        room.Leave(players[1].Id, Now.AddMinutes(1));

        var decision = Apply(room, players[2].Id, ChogRaceMatchRules.Roll, state, new Dice(1));

        Assert.Equal((MatchRuleOutcome.Accepted, true), (decision.Outcome, decision.EndsTurn));
        Assert.False(decision.State!.Value.TryGetProperty("reply", out _));
        Assert.Equal("Move", ViewOf(room, players[0].Id, state)!.Value.GetProperty("phase").GetString());
    }

    private static MatchRuleDecision? TimeOutReply(GameRoom room, string state, ChogRaceMatchRules? rules = null)
    {
        using var document = JsonDocument.Parse(state);
        var decision = (rules ?? Rules).TimeOutReply(new MatchTurnContext(room, document.RootElement, new NoDraws()));
        return decision is null ? null : decision with { State = decision.State?.Clone() };
    }

    [Fact]
    public void An_attack_whose_reply_runs_out_lands_and_the_target_keeps_their_hodl()
    {
        var (room, players) = Started(2);
        var state = ReplyState(room, players, "Rug", [5, 10], [[], ["Hodl"]]);

        var decision = TimeOutReply(room, state)!;

        Assert.Equal((MatchRuleOutcome.Accepted, (Guid?)null), (decision.Outcome, decision.SkipsNextTurnOf));
        Assert.Equal([5, 6], Tiles(decision));
        Assert.Equal(["Hodl"], HandOf(decision, 2));
        Assert.Equal(
            """{"card":"Rug","target":2,"pushed":[{"seat":2,"from":10,"landed":6,"to":6}],"reply":"TimedOut"}""", PlayOf(decision));
        Assert.False(decision.State!.Value.TryGetProperty("reply", out _));
    }

    [Fact]
    public void A_liquidated_whose_reply_runs_out_skips_the_targets_next_turn()
    {
        var (room, players) = Started(2);
        var state = ReplyState(room, players, "Liquidated", [5, 10], [[], ["Hodl"]]);

        Assert.Equal(players[1].Id, TimeOutReply(room, state)!.SkipsNextTurnOf);
    }

    [Fact]
    public void A_reply_timeout_without_an_attack_waiting_changes_nothing()
    {
        var (room, players) = Started(2);

        Assert.Null(TimeOutReply(room, PlayState([5, 10], [["Rug"], []])));
        Assert.Null(TimeOutReply(room, """{"rulesVersion":99,"pawns":[]}"""));

        // The state says an attack waits, but the engine isn't waiting for it.
        var state = CardState([5, 10], [[], ["Hodl"]], [], ["Rug"])[..^1]
            + ""","lastMove":{"seat":1,"roll":1,"from":0,"landed":1,"to":1,"play":{"card":"Rug","target":2}},"reply":{"seat":1,"card":"Rug","target":2}}""";
        Assert.Null(TimeOutReply(room, state));
        Assert.Equal("Move", ViewOf(room, players[0].Id, state)!.Value.GetProperty("phase").GetString());
        Assert.False(ViewOf(room, players[0].Id, state)!.Value.TryGetProperty("reply", out _));
    }

    [Fact]
    public void A_rug_whose_reply_runs_out_onto_a_boost_to_the_finish_wins_for_its_target()
    {
        var boostToFinish = RaceRules.Create(
            2,
            [Start(0), .. Enumerable.Range(1, 25).Select(Normal), Boost(26, to: 29), Normal(27), Normal(28), Finish(29)],
            1,
            3,
            TimeSpan.FromMinutes(20),
            [RaceCard.Rug],
            3,
            RaceRulesV1.Rules.CardValues with { Rug = 2 });
        var rules = new ChogRaceMatchRules([RaceRulesV1.Rules, boostToFinish]);
        var (room, players) = Started(2, rules);
        var state = ReplyState(room, players, "Rug", [10, 28], [[], ["Pump"]]).Replace("\"rulesVersion\":1", "\"rulesVersion\":2");

        var decision = TimeOutReply(room, state, rules)!;

        Assert.Equal(2, decision.State!.Value.GetProperty("winner").GetInt32());
        Assert.Equal([(players[0].Id, 2), (players[1].Id, 1)], decision.Standings!.Select(s => (s.PlayerId, s.Rank)));
    }

    [Fact]
    public void Everyone_sees_the_attack_waiting_for_its_reply_and_who_skips_a_turn()
    {
        var (room, players) = Started(3);
        Skips(room, players[0], players[2]);
        var state = ReplyState(room, players, "Rug", [5, 10, 2], [[], ["Hodl", "Pump"], []]);

        var views = players.Select(p => ViewOf(room, p.Id, state)!.Value).ToArray();

        foreach (var view in views)
        {
            Assert.Equal("Reply", view.GetProperty("phase").GetString());
            Assert.Equal("""{"seat":1,"card":"Rug","target":2}""", view.GetProperty("reply").GetRawText());
            Assert.Equal("[3]", view.GetProperty("skipping").GetRawText());
        }

        Assert.Equal(["Hodl", "Pump"], Cards(views[1].GetProperty("hand")));
        Assert.Equal(["Pump"], Cards(views[1].GetProperty("playable")));
    }
}
