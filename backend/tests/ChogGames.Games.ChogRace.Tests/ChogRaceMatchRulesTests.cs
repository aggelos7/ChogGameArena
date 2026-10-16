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
/// from the discard pile and leavers' hands; each player sees only their own cards.
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
            3);
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
        GameRoom room, Guid player, string type, string state, IRandomSource random, string payload = "{}")
    {
        using var stateJson = JsonDocument.Parse(state);
        using var payloadJson = JsonDocument.Parse(payload);
        var decision = Rules.Apply(new MatchCommandContext(room, player, type, payloadJson.RootElement, stateJson.RootElement, random));
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
    [InlineData(7, 2, 9, 7, "Setback")]
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
    [InlineData("PLAY_CARD")]
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

        foreach (var type in new[] { ChogRaceMatchRules.Roll, ChogRaceMatchRules.EndTurn })
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
        var moved = Apply(room, players[0].Id, ChogRaceMatchRules.Roll, room.State!, new Dice(2)).State!.Value.GetRawText();
        var playing = StateWith([3, 0, 0], ""","playPhase":1""");

        var views = players.Skip(1).Select(p => ViewOf(room, p.Id, moved)!.Value.GetRawText()).ToArray();

        Assert.Single(views.Distinct());
        var view = ViewOf(room, players[1].Id, moved)!.Value;
        Assert.Equal("""{"seat":1,"roll":2,"from":0,"landed":2,"to":2,"effect":"Card","draw":"Drawn"}""", view.GetProperty("lastMove").GetRawText());
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
            room, players[0].Id, ChogRaceMatchRules.Roll, CardState([0, 0], [["Rug"], []], ["Pump", "Whale"], ["Fomo"]), dice);

        Assert.Equal((MatchRuleOutcome.Accepted, true), (decision.Outcome, decision.EndsTurn));
        Assert.Single(dice.Draws);
        Assert.Equal(["Rug", "Pump"], HandOf(decision, 1));
        Assert.Empty(HandOf(decision, 2));
        Assert.Equal(["Whale"], Pile(decision, "deck"));
        Assert.Equal(["Fomo"], Pile(decision, "discard"));
        Assert.False(decision.State!.Value.TryGetProperty("playPhase", out _));
        Assert.Equal(
            """{"seat":1,"roll":2,"from":0,"landed":2,"to":2,"effect":"Card","draw":"Drawn","card":"Pump"}""",
            LastMove(decision));
    }

    [Fact]
    public void A_full_hand_draws_nothing_and_leaves_the_deck_alone()
    {
        var (room, players) = Started(2);
        var state = CardState([0, 0], [["Rug", "Hodl", "Pump"], []], ["Pump"], []);

        var decision = Apply(room, players[0].Id, ChogRaceMatchRules.Roll, state, new Dice(2));

        Assert.True(decision.EndsTurn);
        Assert.Equal(["Rug", "Hodl", "Pump"], HandOf(decision, 1));
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
    public void Holding_cards_doesnt_open_the_play_phase_while_none_can_be_played()
    {
        var (room, players) = Started(2);

        var decision = Apply(
            room, players[0].Id, ChogRaceMatchRules.Roll, CardState([0, 0], [["Pump", "Hodl"], []], ["Whale"], []), new Dice(1));

        Assert.True(decision.EndsTurn);
        Assert.False(decision.State!.Value.TryGetProperty("playPhase", out _));
        Assert.Equal(["Pump", "Hodl"], HandOf(decision, 1));
    }
}
