using System.Text.Json;
using ChogGames.Core.Games;
using ChogGames.Core.Matches;
using ChogGames.Core.Players;
using ChogGames.Core.Rooms;
using ChogGames.Games.ChogLife.Rules;

namespace ChogGames.Games.ChogLife.Tests;

/// <summary>
/// STORY-036 to STORY-039 (DEC-038 to DEC-041): a Chog Life match starts with every pawn on the start tile with the
/// starting MON under the current rules version, and every player sees that version's board and the pawns, with their
/// MON, of the players still playing. A roll moves the pawn and resolves the tile it stops on; a roll that stops on a
/// fork keeps the turn until the player chooses a lane, and a turn that runs out first takes the default lane. The lane
/// taken holds a stake that pays out when the pawn rejoins the main track, a loss beyond the liquid MON held becomes a
/// liability, and every player sees each player's portfolio (STORY-040, DEC-042).
/// </summary>
public class ChogLifeMatchRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

    private static readonly ChogLifeMatchRules Rules = new([LifeRulesV1.Rules]);

    /// <summary>Never reorders a shuffle, so seats follow join order (host first).</summary>
    private sealed class KeepOrder : IRandomSource
    {
        public int Next(int minInclusive, int maxExclusive) => maxExclusive - 1;
    }

    private static Player Guest(int n)
    {
        Assert.True(PlayerDisplayName.TryCreate($"Player {n}", out var name));
        return Player.CreateGuest(name, Now);
    }

    /// <summary>A started Chog Life match of <paramref name="count"/> players, with the rules' start state stored.</summary>
    private static (GameRoom Room, Player[] Players) Started(int count, ChogLifeMatchRules? rules = null)
    {
        rules ??= Rules;
        var players = Enumerable.Range(1, count).Select(Guest).ToArray();
        Assert.True(RoomCode.TryParse("K7QF3M", out var code));
        var room = GameRoom.Create(GameDefinition.Create(GameDefinition.ChogLife, 2, 6), players[0], code, Now);
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

    private static JsonElement View(GameRoom room, Guid viewer, ChogLifeMatchRules? rules = null)
    {
        using var state = JsonDocument.Parse(room.State!);
        var view = (rules ?? Rules).ViewFor(new MatchViewContext(room, viewer, state.RootElement));
        Assert.NotNull(view);
        return view.Value.Clone();
    }

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
        Assert.Equal(
            Enumerable.Range(1, count).Select(seat => (seat, 0)),
            Pawns(state.RootElement));
    }

    [Fact]
    public void Every_player_sees_the_same_board_and_pawns()
    {
        var (room, players) = Started(3);

        var views = players.Select(p => View(room, p.Id).GetRawText()).Distinct().ToArray();

        Assert.Single(views);
        var view = View(room, players[0].Id);
        Assert.Equal(1, view.GetProperty("rulesVersion").GetInt32());
        Assert.Equal([(1, 0), (2, 0), (3, 0)], Pawns(view));
    }

    [Fact]
    public void The_board_shows_every_tile_with_its_type_lane_links_and_values()
    {
        var (room, players) = Started(2);

        var tiles = View(room, players[0].Id).GetProperty("tiles").EnumerateArray().ToArray();

        Assert.Equal(LifeRulesV1.Rules.Tiles.Count, tiles.Length);
        Assert.Equal(
            """{"index":0,"type":"Start","lane":"Main","next":1}""",
            tiles[0].GetRawText());
        Assert.Equal(
            """{"index":2,"type":"Reward","lane":"Main","next":3,"amount":20}""",
            tiles[2].GetRawText());
        Assert.Equal(
            """{"index":3,"type":"Event","lane":"Main","next":4,"event":"Airdrop"}""",
            tiles[3].GetRawText());
        Assert.Equal(
            """{"index":9,"type":"Branch","lane":"Main","safeNext":10,"degenNext":14}""",
            tiles[9].GetRawText());
        Assert.Equal(
            """{"index":14,"type":"Event","lane":"Degen","next":15,"event":"Rug"}""",
            tiles[14].GetRawText());
        Assert.Equal(
            """{"index":35,"type":"Finish","lane":"Main"}""",
            tiles[35].GetRawText());
        Assert.All(tiles.Select((tile, i) => (tile, i)), t => Assert.Equal(t.i, t.tile.GetProperty("index").GetInt32()));
    }

    [Fact]
    public void Pawns_of_players_who_left_are_taken_off_the_board()
    {
        var (room, players) = Started(3);

        room.Leave(players[1].Id, Now.AddMinutes(2));

        Assert.Equal([(1, 0), (3, 0)], Pawns(View(room, players[0].Id)));
        Assert.Equal([(1, 0), (3, 0)], Pawns(View(room, players[1].Id)));
    }

    [Fact]
    public void A_match_keeps_the_board_of_the_version_it_started_with()
    {
        var (room, players) = Started(2);
        var version2 = LifeRules.Create(
            2,
            LifeRulesV1.Rules.Tiles.Select(tile => tile.Index == 2 ? tile with { Amount = 99 } : tile),
            startingMon: 100,
            finishBonus: 100,
            timeCap: TimeSpan.FromMinutes(20),
            dieFaces: 6,
            events: LifeRulesV1.Rules.Events.Values);
        var both = new ChogLifeMatchRules([LifeRulesV1.Rules, version2]);

        var view = View(room, players[0].Id, both);

        Assert.Equal(1, view.GetProperty("rulesVersion").GetInt32());
        Assert.Equal(20, view.GetProperty("tiles")[2].GetProperty("amount").GetInt32());

        var (newRoom, newPlayers) = Started(2, both);
        var newView = View(newRoom, newPlayers[0].Id, both);
        Assert.Equal(2, newView.GetProperty("rulesVersion").GetInt32());
        Assert.Equal(99, newView.GetProperty("tiles")[2].GetProperty("amount").GetInt32());
    }

    [Theory]
    [InlineData("""{"rulesVersion":7,"pawns":[{"seat":1,"tile":0}]}""")]
    [InlineData("""{"rulesVersion":1}""")]
    [InlineData("""{"rulesVersion":"one","pawns":[]}""")]
    [InlineData("""[1,2,3]""")]
    public void A_state_it_cant_read_shows_nothing(string state)
    {
        var (room, players) = Started(2);
        using var document = JsonDocument.Parse(state);

        Assert.Null(Rules.ViewFor(new MatchViewContext(room, players[0].Id, document.RootElement)));
    }

    /// <summary>Returns the queued rolls in order and records every draw.</summary>
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

    private static readonly JsonElement NoPayload = JsonSerializer.SerializeToElement(new { });

    private static MatchRuleDecision Roll(GameRoom room, Player player, string? state, IRandomSource dice, JsonElement? payload = null)
    {
        using var document = state is null ? null : JsonDocument.Parse(state);
        var decision = Rules.Apply(new MatchCommandContext(
            room, player.Id, ChogLifeMatchRules.Roll, payload ?? NoPayload, document?.RootElement.Clone(), dice));
        return decision;
    }

    private static int[] Mon(JsonElement state) =>
        state.GetProperty("pawns").EnumerateArray().Select(pawn => pawn.GetProperty("mon").GetInt32()).ToArray();

    private static string StateWithMon(params (int Seat, int Tile, int Mon)[] pawns) =>
        JsonSerializer.Serialize(new { rulesVersion = 1, pawns = pawns.Select(p => new { seat = p.Seat, tile = p.Tile, mon = p.Mon }) });

    private static string StateWith(params (int Seat, int Tile)[] pawns) =>
        JsonSerializer.Serialize(new { rulesVersion = 1, pawns = pawns.Select(p => new { seat = p.Seat, tile = p.Tile }) });

    private static JsonElement LastMove(MatchRuleDecision decision) => decision.State!.Value.GetProperty("lastMove");

    [Fact]
    public void A_roll_draws_one_die_moves_the_pawn_and_ends_the_turn()
    {
        var (room, players) = Started(3);
        var dice = new Dice(4);

        var decision = Roll(room, players[0], room.State, dice);

        Assert.Equal((MatchRuleOutcome.Accepted, true), (decision.Outcome, decision.EndsTurn));
        Assert.Equal([(1, 7)], dice.Draws);
        Assert.Equal([(1, 4), (2, 0), (3, 0)], Pawns(decision.State!.Value));
        Assert.Equal("""{"seat":1,"roll":4,"from":0,"to":4,"path":[1,2,3,4]}""", LastMove(decision).GetRawText());
        Assert.Equal(1, decision.State.Value.GetProperty("rulesVersion").GetInt32());
        Assert.Null(decision.Standings);
    }

    [Fact]
    public void A_roll_in_the_payload_is_ignored()
    {
        var (room, players) = Started(2);
        var forged = JsonSerializer.SerializeToElement(new { roll = 6, to = 35, seat = 2 });

        var decision = Roll(room, players[1], room.State, new Dice(2), forged);

        Assert.Equal([(1, 0), (2, 2)], Pawns(decision.State!.Value));
        Assert.Equal(2, LastMove(decision).GetProperty("roll").GetInt32());
    }

    private static MatchRuleDecision Choose(GameRoom room, Player player, string? state, object? payload)
    {
        using var document = state is null ? null : JsonDocument.Parse(state);
        return Rules.Apply(new MatchCommandContext(
            room,
            player.Id,
            ChogLifeMatchRules.ChooseLane,
            JsonSerializer.SerializeToElement(payload),
            document?.RootElement.Clone(),
            new Dice()));
    }

    private static string Lane(object lane) => JsonSerializer.Serialize(new { lane });

    private static string? TimeOut(GameRoom room, string state)
    {
        using var document = JsonDocument.Parse(state);
        return Rules.TimeOutTurn(new MatchTurnContext(room, document.RootElement, new Dice()))?.GetRawText();
    }

    /// <summary>Seat 1 rolls 6 from tile 6 onto the first fork (tile 9) and is choosing a lane.</summary>
    private static (GameRoom Room, Player[] Players, string State) AtFork(int count = 2)
    {
        var (room, players) = Started(count);
        var toBranch = Roll(room, players[0], StateWith((1, 6), (2, 0)), new Dice(6));
        return (room, players, toBranch.State!.Value.GetRawText());
    }

    [Fact]
    public void A_move_that_stops_on_a_branch_keeps_the_turn_for_the_lane_choice()
    {
        var (room, players) = Started(2);

        var toBranch = Roll(room, players[0], StateWith((1, 6), (2, 0)), new Dice(6));

        Assert.Equal((MatchRuleOutcome.Accepted, false), (toBranch.Outcome, toBranch.EndsTurn));
        Assert.Equal("""{"seat":1,"roll":6,"from":6,"to":9,"path":[7,8,9]}""", LastMove(toBranch).GetRawText());
        Assert.Equal(1, toBranch.State!.Value.GetProperty("choosing").GetInt32());
        Assert.Equal([(1, 9), (2, 0)], Pawns(toBranch.State.Value));
    }

    [Theory]
    [InlineData("Safe", 2, new[] { 10, 11 }, 0)]
    [InlineData("Degen", 2, new[] { 14, 15 }, 60)]
    public void The_chosen_lane_ends_the_turn_and_the_next_roll_leaves_by_it(string lane, int roll, int[] path, int change)
    {
        var (room, players, state) = AtFork();

        var chosen = Choose(room, players[0], state, new { lane });

        Assert.Equal((MatchRuleOutcome.Accepted, true), (chosen.Outcome, chosen.EndsTurn));
        Assert.False(chosen.State!.Value.TryGetProperty("choosing", out _));
        Assert.Equal(lane, chosen.State.Value.GetProperty("pawns")[0].GetProperty("lane").GetString());
        Assert.Equal(
            $$"""{"seat":1,"roll":6,"from":6,"to":9,"path":[7,8,9],"lane":"{{lane}}","staked":50}""",
            LastMove(chosen).GetRawText());

        var dice = new Dice(roll);
        var off = Roll(room, players[0], chosen.State.Value.GetRawText(), dice);

        // The stake stays on the lane until the pawn rejoins the main track.
        Assert.Equal([(1, 7)], dice.Draws);
        Assert.Equal(path, LastMove(off).GetProperty("path").EnumerateArray().Select(t => t.GetInt32()));
        Assert.Equal(50 + change, Mon(off.State!.Value)[0]);
        Assert.False(off.State.Value.GetProperty("pawns")[0].TryGetProperty("lane", out _));
        Assert.Equal(
            $$"""{"lane":"{{lane}}","amount":50}""",
            off.State.Value.GetProperty("pawns")[0].GetProperty("stake").GetRawText());
    }

    [Theory]
    [InlineData("Main")]
    [InlineData("safe")]
    [InlineData("DEGEN")]
    [InlineData("")]
    [InlineData(1)]
    [InlineData(null)]
    public void Only_the_offered_lanes_are_accepted(object? lane)
    {
        var (room, players, state) = AtFork();

        var decision = Choose(room, players[0], state, new { lane });

        Assert.Equal((MatchRuleOutcome.Rejected, "invalid_lane"), (decision.Outcome, decision.Reason));
    }

    [Fact]
    public void A_choice_without_a_lane_is_refused()
    {
        var (room, players, state) = AtFork();

        foreach (var payload in new object[] { new { }, new { route = "Degen" }, new[] { "Degen" }, "Degen" })
        {
            Assert.Equal("invalid_lane", Choose(room, players[0], state, payload).Reason);
        }
    }

    [Fact]
    public void A_roll_while_choosing_is_refused_without_a_draw()
    {
        var (room, players, state) = AtFork();
        var dice = new Dice();

        var decision = Roll(room, players[0], state, dice);

        Assert.Equal((MatchRuleOutcome.Rejected, "choose_lane_first"), (decision.Outcome, decision.Reason));
        Assert.Empty(dice.Draws);
    }

    [Fact]
    public void A_choice_with_none_pending_is_refused()
    {
        var (room, players, state) = AtFork();
        var chosen = Choose(room, players[0], state, new { lane = "Degen" }).State!.Value.GetRawText();

        Assert.Equal("no_choice_pending", Choose(room, players[0], room.State, new { lane = "Safe" }).Reason);
        Assert.Equal("no_choice_pending", Choose(room, players[0], chosen, new { lane = "Safe" }).Reason);
        Assert.Equal("no_choice_pending", Choose(room, players[1], state, new { lane = "Safe" }).Reason);
        Assert.Equal("no_choice_pending", Choose(room, players[0], StateWith((1, 9), (2, 0)), new { lane = "Degen" }).Reason);
    }

    [Fact]
    public void A_choice_marked_on_a_pawn_off_a_fork_is_refused()
    {
        var (room, players) = Started(2);
        var forged = """{"rulesVersion":1,"pawns":[{"seat":1,"tile":4,"mon":100},{"seat":2,"tile":0,"mon":100}],"choosing":1}""";

        Assert.Equal("no_choice_pending", Choose(room, players[0], forged, new { lane = "Degen" }).Reason);
    }

    [Theory]
    [InlineData("Main")]
    [InlineData("Sideways")]
    public void A_pawn_with_a_lane_it_cant_take_is_unplayable(string lane)
    {
        var (room, players) = Started(2);
        var state = $$"""{"rulesVersion":1,"pawns":[{"seat":1,"tile":9,"mon":100,"lane":"{{lane}}"},{"seat":2,"tile":0,"mon":100}]}""";
        var dice = new Dice();

        Assert.Equal("game_state_unavailable", Roll(room, players[0], state, dice).Reason);
        Assert.Empty(dice.Draws);
    }

    [Fact]
    public void A_pawn_on_a_branch_with_no_lane_leaves_by_the_default_one()
    {
        // A match from before STORY-039 leaves its pawns on a branch with no lane and no choice pending.
        var (room, players) = Started(2);

        var decision = Roll(room, players[0], StateWith((1, 9), (2, 0)), new Dice(2));

        Assert.Equal("""{"seat":1,"roll":2,"from":9,"to":11,"path":[10,11]}""", LastMove(decision).GetRawText());
        Assert.True(decision.EndsTurn);
    }

    [Fact]
    public void A_turn_that_runs_out_while_choosing_takes_the_default_safe_lane()
    {
        var (room, players, state) = AtFork();

        var timedOut = TimeOut(room, state);

        Assert.NotNull(timedOut);
        using var document = JsonDocument.Parse(timedOut);
        Assert.False(document.RootElement.TryGetProperty("choosing", out _));
        Assert.Equal("Safe", document.RootElement.GetProperty("pawns")[0].GetProperty("lane").GetString());
        Assert.Equal(
            """{"seat":1,"roll":6,"from":6,"to":9,"path":[7,8,9],"lane":"Safe","byDefault":true,"staked":50}""",
            document.RootElement.GetProperty("lastMove").GetRawText());

        var off = Roll(room, players[0], timedOut, new Dice(1));
        Assert.Equal([10], LastMove(off).GetProperty("path").EnumerateArray().Select(t => t.GetInt32()));
    }

    [Fact]
    public void A_degen_default_lane_is_taken_when_configured()
    {
        var degenFirst = LifeRules.Create(
            2,
            LifeRulesV1.Rules.Tiles,
            startingMon: 100,
            finishBonus: 100,
            timeCap: TimeSpan.FromMinutes(20),
            dieFaces: 6,
            events: LifeRulesV1.Rules.Events.Values,
            defaultLane: LifeLane.Degen);
        var rules = new ChogLifeMatchRules([degenFirst]);
        var (room, _) = Started(2, rules);
        using var state = JsonDocument.Parse(
            """{"rulesVersion":2,"pawns":[{"seat":1,"tile":9,"mon":100},{"seat":2,"tile":0,"mon":100}],"choosing":1}""");

        var timedOut = rules.TimeOutTurn(new MatchTurnContext(room, state.RootElement, new Dice()))!.Value;

        Assert.Equal("Degen", timedOut.GetProperty("pawns")[0].GetProperty("lane").GetString());
        Assert.Equal("Degen", rules.ViewFor(new MatchViewContext(room, room.CurrentPlayerId!.Value, timedOut))!.Value
            .GetProperty("defaultLane").GetString());
    }

    [Fact]
    public void A_turn_that_runs_out_with_no_choice_pending_is_skipped()
    {
        var (room, players, state) = AtFork();
        var chosen = Choose(room, players[0], state, new { lane = "Degen" }).State!.Value.GetRawText();

        Assert.Null(TimeOut(room, chosen));
        Assert.Null(TimeOut(room, StateWith((1, 9), (2, 0))));
        Assert.Null(TimeOut(room, """{"rulesVersion":7,"pawns":[{"seat":1,"tile":9}],"choosing":1}"""));
        Assert.Null(TimeOut(room, """{"rulesVersion":1,"pawns":[{"seat":1,"tile":4},{"seat":2,"tile":0}],"choosing":1}"""));
    }

    [Fact]
    public void A_choice_left_by_a_player_who_is_not_on_turn_is_never_resolved_for_them()
    {
        // Seat 2 is marked choosing but it's seat 1's turn (seat 2 left mid-choice and the turn moved on): the timeout
        // doesn't choose for seat 2, and seat 1's roll goes ahead and clears the stale mark.
        var (room, players) = Started(3);
        var stale = """{"rulesVersion":1,"pawns":[{"seat":1,"tile":0,"mon":100},{"seat":2,"tile":9,"mon":100},{"seat":3,"tile":0,"mon":100}],"choosing":2}""";

        Assert.Null(TimeOut(room, stale));
        var decision = Roll(room, players[0], stale, new Dice(1));

        Assert.Equal(MatchRuleOutcome.Accepted, decision.Outcome);
        Assert.False(decision.State!.Value.TryGetProperty("choosing", out _));
    }

    [Fact]
    public void Every_player_sees_who_is_choosing_and_the_default_lane()
    {
        var (room, players, state) = AtFork(3);
        using var document = JsonDocument.Parse(state);

        var views = players
            .Select(p => Rules.ViewFor(new MatchViewContext(room, p.Id, document.RootElement))!.Value)
            .ToArray();

        Assert.Single(views.Select(v => v.GetRawText()).Distinct());
        Assert.Equal(1, views[0].GetProperty("choosing").GetInt32());
        Assert.Equal("Safe", views[0].GetProperty("defaultLane").GetString());

        room.Leave(players[0].Id, Now.AddMinutes(2));
        var afterLeaving = Rules.ViewFor(new MatchViewContext(room, players[1].Id, document.RootElement))!.Value;
        Assert.False(afterLeaving.TryGetProperty("choosing", out _));
    }

    [Fact]
    public void A_roll_past_the_finish_stops_on_it_and_a_finished_pawn_passes_without_a_roll()
    {
        var (room, players) = Started(2);

        var finished = Roll(room, players[1], StateWith((1, 0), (2, 33)), new Dice(6));
        Assert.Equal([(1, 0), (2, 35)], Pawns(finished.State!.Value));

        var dice = new Dice();
        var passed = Roll(room, players[1], finished.State.Value.GetRawText(), dice);

        Assert.Equal((MatchRuleOutcome.Accepted, true), (passed.Outcome, passed.EndsTurn));
        Assert.Empty(dice.Draws);
        Assert.Equal([(1, 0), (2, 35)], Pawns(passed.State!.Value));
        Assert.Equal("""{"seat":2,"from":35,"to":35,"path":[]}""", LastMove(passed).GetRawText());
    }

    [Fact]
    public void A_match_without_a_state_starts_from_the_start_tile()
    {
        // A match started before STORY-036 has no state; its first roll sets every pawn out from the start.
        var (room, players) = Started(2);

        var decision = Roll(room, players[0], null, new Dice(3, 0));

        Assert.Equal([(1, 3), (2, 0)], Pawns(decision.State!.Value));
        Assert.Equal([110, 100], Mon(decision.State.Value));
    }

    [Theory]
    [InlineData("""{"rulesVersion":7,"pawns":[{"seat":1,"tile":0},{"seat":2,"tile":0}]}""")]
    [InlineData("""{"rulesVersion":1,"pawns":[{"seat":2,"tile":0}]}""")]
    [InlineData("""{"rulesVersion":1,"pawns":[{"seat":1,"tile":40},{"seat":2,"tile":0}]}""")]
    [InlineData("""{"rulesVersion":1}""")]
    public void A_roll_on_a_state_it_cant_play_is_rejected_without_a_draw(string state)
    {
        var (room, players) = Started(2);
        var dice = new Dice();

        var decision = Roll(room, players[0], state, dice);

        Assert.Equal((MatchRuleOutcome.Rejected, "game_state_unavailable"), (decision.Outcome, decision.Reason));
        Assert.Empty(dice.Draws);
    }

    [Fact]
    public void Every_player_sees_the_same_last_move()
    {
        var (room, players) = Started(3);
        var decision = Roll(room, players[0], room.State, new Dice(5));
        using var state = JsonDocument.Parse(decision.State!.Value.GetRawText());

        var moves = players
            .Select(p => Rules.ViewFor(new MatchViewContext(room, p.Id, state.RootElement))!.Value.GetProperty("lastMove").GetRawText())
            .Distinct();

        Assert.Equal(["""{"seat":1,"roll":5,"from":0,"to":5,"path":[1,2,3,4,5],"change":-15}"""], moves);
    }

    [Fact]
    public void Other_commands_are_unknown_and_a_turn_that_runs_out_is_skipped()
    {
        var (room, players) = Started(2);
        using var state = JsonDocument.Parse(room.State!);

        foreach (var type in new[] { "MOVE", "END_TURN", "roll" })
        {
            var decision = Rules.Apply(new MatchCommandContext(room, players[0].Id, type, NoPayload, state.RootElement, new Dice()));
            Assert.Equal(MatchRuleOutcome.UnknownCommand, decision.Outcome);
        }

        Assert.Null(Rules.TimeOutTurn(new MatchTurnContext(room, state.RootElement, new Dice())));
    }

    [Fact]
    public void Rules_need_a_version_and_play_chog_life()
    {
        Assert.Throws<ArgumentException>(() => new ChogLifeMatchRules([]));
        Assert.Equal(GameDefinition.ChogLife, Rules.GameType);
    }

    [Fact]
    public void Every_player_starts_with_the_starting_mon_and_everyone_sees_it()
    {
        var (room, players) = Started(3);

        using var state = JsonDocument.Parse(room.State!);
        Assert.Equal([100, 100, 100], Mon(state.RootElement));
        Assert.All(players, p => Assert.Equal([100, 100, 100], Mon(View(room, p.Id))));
    }

    [Fact]
    public void Stopping_on_a_reward_tile_pays_its_amount_without_another_draw()
    {
        var (room, players) = Started(2);
        var dice = new Dice(2);

        var decision = Roll(room, players[0], room.State, dice);

        Assert.Equal([(1, 7)], dice.Draws);
        Assert.Equal([120, 100], Mon(decision.State!.Value));
        Assert.Equal("""{"seat":1,"roll":2,"from":0,"to":2,"path":[1,2],"change":20}""", LastMove(decision).GetRawText());
    }

    [Fact]
    public void Only_the_tile_where_the_pawn_stops_resolves()
    {
        // Rolling 4 passes the +20 reward and the airdrop and stops on a normal tile.
        var (room, players) = Started(2);
        var dice = new Dice(4);

        var decision = Roll(room, players[0], room.State, dice);

        Assert.Equal([(1, 7)], dice.Draws);
        Assert.Equal([100, 100], Mon(decision.State!.Value));
        Assert.False(LastMove(decision).TryGetProperty("change", out _));
    }

    [Fact]
    public void An_event_draws_its_outcome_after_the_die()
    {
        var (room, players) = Started(2);
        var dice = new Dice(3, 95);

        var decision = Roll(room, players[1], room.State, dice);

        Assert.Equal([(1, 7), (0, 100)], dice.Draws);
        Assert.Equal([100, 150], Mon(decision.State!.Value));
        Assert.Equal(50, LastMove(decision).GetProperty("change").GetInt32());
    }

    [Theory]
    [InlineData(100, 60, -40)]
    [InlineData(5, 3, -2)]
    [InlineData(0, 0, 0)]
    public void A_rug_takes_its_share_and_a_balance_never_goes_below_zero(int before, int after, int change)
    {
        var (room, players) = Started(2);

        var decision = Roll(room, players[0], StateWithMon((1, 21, before), (2, 0, 70)), new Dice(3));

        Assert.Equal([(1, 24), (2, 0)], Pawns(decision.State!.Value));
        Assert.Equal([after, 70], Mon(decision.State.Value));
        Assert.Equal(change, LastMove(decision).GetProperty("change").GetInt32());
    }

    [Fact]
    public void A_loss_bigger_than_the_balance_leaves_the_rest_as_a_liability()
    {
        var (room, players) = Started(2);

        var decision = Roll(room, players[0], StateWithMon((1, 0, 9), (2, 0, 100)), new Dice(5));

        Assert.Equal([0, 100], Mon(decision.State!.Value));
        Assert.Equal(6, decision.State.Value.GetProperty("pawns")[0].GetProperty("liabilities").GetInt32());
        Assert.Equal(-15, LastMove(decision).GetProperty("change").GetInt32());
    }

    [Fact]
    public void Pawns_from_before_mon_was_tracked_hold_the_starting_mon()
    {
        var (room, players) = Started(2);
        var old = StateWith((1, 0), (2, 4));
        using var document = JsonDocument.Parse(old);

        var view = Rules.ViewFor(new MatchViewContext(room, players[0].Id, document.RootElement))!.Value;
        var decision = Roll(room, players[0], old, new Dice(5));

        Assert.Equal([100, 100], Mon(view));
        Assert.Equal([85, 100], Mon(decision.State!.Value));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(LifeRules.MaxMon + 1)]
    public void A_roll_with_an_invalid_balance_is_rejected_without_a_draw(int mon)
    {
        var (room, players) = Started(2);
        var dice = new Dice();

        var decision = Roll(room, players[0], StateWithMon((1, 0, mon), (2, 0, 100)), dice);

        Assert.Equal((MatchRuleOutcome.Rejected, "game_state_unavailable"), (decision.Outcome, decision.Reason));
        Assert.Empty(dice.Draws);
    }

    [Fact]
    public void A_pass_at_the_finish_changes_no_mon()
    {
        var (room, players) = Started(2);

        var decision = Roll(room, players[0], StateWithMon((1, 35, 140), (2, 0, 100)), new Dice());

        Assert.Equal([140, 100], Mon(decision.State!.Value));
        Assert.False(LastMove(decision).TryGetProperty("change", out _));
    }

    private static JsonElement Pawn(MatchRuleDecision decision, int index = 0) =>
        decision.State!.Value.GetProperty("pawns")[index];

    [Theory]
    [InlineData(50, 0, 50)]
    [InlineData(30, 0, 30)]
    [InlineData(1, 0, 1)]
    public void Choosing_a_lane_stakes_the_fork_stake_or_all_the_liquid_mon_held(int mon, int left, int staked)
    {
        var (room, players) = Started(2);
        var state = $$"""{"rulesVersion":1,"pawns":[{"seat":1,"tile":9,"mon":{{mon}}},{"seat":2,"tile":0,"mon":100}],"choosing":1}""";

        var chosen = Choose(room, players[0], state, new { lane = "Degen" });

        Assert.Equal(left, Pawn(chosen).GetProperty("mon").GetInt32());
        Assert.Equal($$"""{"lane":"Degen","amount":{{staked}}}""", Pawn(chosen).GetProperty("stake").GetRawText());
    }

    [Fact]
    public void Choosing_with_no_liquid_mon_stakes_nothing()
    {
        var (room, players) = Started(2);
        var state = """{"rulesVersion":1,"pawns":[{"seat":1,"tile":9,"mon":0,"liabilities":6},{"seat":2,"tile":0,"mon":100}],"lastMove":{"seat":1,"roll":6,"from":6,"to":9,"path":[7,8,9]},"choosing":1}""";

        var chosen = Choose(room, players[0], state, new { lane = "Degen" });

        Assert.Equal(MatchRuleOutcome.Accepted, chosen.Outcome);
        Assert.Equal("""{"seat":1,"tile":9,"mon":0,"lane":"Degen","liabilities":6}""", Pawn(chosen).GetRawText());
        Assert.Equal("""{"seat":1,"roll":6,"from":6,"to":9,"path":[7,8,9],"lane":"Degen"}""", LastMove(chosen).GetRawText());
    }

    [Fact]
    public void A_turn_that_runs_out_stakes_on_the_default_lane()
    {
        var (room, _) = Started(2);

        var timedOut = TimeOut(room, """{"rulesVersion":1,"pawns":[{"seat":1,"tile":9,"mon":20},{"seat":2,"tile":0,"mon":100}],"choosing":1}""");

        using var document = JsonDocument.Parse(timedOut!);
        Assert.Equal(
            """{"seat":1,"tile":9,"mon":0,"lane":"Safe","stake":{"lane":"Safe","amount":20}}""",
            document.RootElement.GetProperty("pawns")[0].GetRawText());
    }

    [Fact]
    public void A_safe_stake_pays_back_as_the_pawn_rejoins_the_main_track()
    {
        var (room, players) = Started(2);
        var state = """{"rulesVersion":1,"pawns":[{"seat":1,"tile":13,"mon":50,"stake":{"lane":"Safe","amount":50}},{"seat":2,"tile":0,"mon":100}]}""";
        var dice = new Dice(1);

        var decision = Roll(room, players[0], state, dice);

        Assert.Equal([(1, 7)], dice.Draws);
        Assert.Equal("""{"seat":1,"tile":18,"mon":112}""", Pawn(decision).GetRawText());
        Assert.Equal(
            """{"seat":1,"roll":1,"from":13,"to":18,"path":[18],"matured":{"lane":"Safe","stake":50,"payout":62}}""",
            LastMove(decision).GetRawText());
    }

    [Theory]
    [InlineData(0, 150, 220)]
    [InlineData(30, 10, 66)]
    public void A_degen_stake_draws_its_return_before_the_tile_it_stops_on(int stakeDraw, int payout, int mon)
    {
        // From the last degen tile, 2 steps rejoin the main track and stop on a market (+10%, drawn 0).
        var (room, players) = Started(2);
        var state = """{"rulesVersion":1,"pawns":[{"seat":1,"tile":17,"mon":50,"stake":{"lane":"Degen","amount":50}},{"seat":2,"tile":0,"mon":100}]}""";
        var dice = new Dice(2, stakeDraw, 0);

        var decision = Roll(room, players[0], state, dice);

        Assert.Equal([(1, 7), (0, 100), (0, 100)], dice.Draws);
        Assert.Equal(mon, Mon(decision.State!.Value)[0]);
        Assert.False(Pawn(decision).TryGetProperty("stake", out _));
        Assert.Equal(payout, LastMove(decision).GetProperty("matured").GetProperty("payout").GetInt32());
        Assert.Equal(mon - 50 - payout, LastMove(decision).GetProperty("change").GetInt32());
    }

    [Fact]
    public void A_payout_and_a_gain_repay_liabilities_first()
    {
        var (room, players) = Started(2);

        var paidOut = Roll(room, players[0],
            """{"rulesVersion":1,"pawns":[{"seat":1,"tile":13,"mon":0,"stake":{"lane":"Safe","amount":50},"liabilities":30},{"seat":2,"tile":0,"mon":100}]}""",
            new Dice(1));
        var rewarded = Roll(room, players[0],
            """{"rulesVersion":1,"pawns":[{"seat":1,"tile":0,"mon":0,"liabilities":6},{"seat":2,"tile":0,"mon":100}]}""",
            new Dice(2));

        Assert.Equal("""{"seat":1,"tile":18,"mon":32}""", Pawn(paidOut).GetRawText());
        Assert.Equal("""{"seat":1,"tile":2,"mon":14}""", Pawn(rewarded).GetRawText());
        Assert.Equal(20, LastMove(rewarded).GetProperty("change").GetInt32());
    }

    [Fact]
    public void A_loss_in_debt_adds_to_the_liabilities()
    {
        // A liquidation takes at least 20 MON, even from a player with none.
        var (room, players) = Started(2);

        var decision = Roll(room, players[0],
            """{"rulesVersion":1,"pawns":[{"seat":1,"tile":14,"mon":0,"liabilities":6},{"seat":2,"tile":0,"mon":100}]}""",
            new Dice(2));

        Assert.Equal("""{"seat":1,"tile":16,"mon":0,"liabilities":26}""", Pawn(decision).GetRawText());
        Assert.Equal(-20, LastMove(decision).GetProperty("change").GetInt32());
    }

    [Fact]
    public void Every_player_sees_each_players_portfolio_as_the_server_values_it()
    {
        var (room, players) = Started(3);
        using var document = JsonDocument.Parse(
            """{"rulesVersion":1,"pawns":[{"seat":1,"tile":10,"mon":40,"stake":{"lane":"Safe","amount":50}},{"seat":2,"tile":5,"mon":0,"liabilities":6},{"seat":3,"tile":0}]}""");

        var views = players
            .Select(p => Rules.ViewFor(new MatchViewContext(room, p.Id, document.RootElement))!.Value)
            .ToArray();

        Assert.Single(views.Select(v => v.GetRawText()).Distinct());
        Assert.Equal(
            """[{"seat":1,"tile":10,"mon":40,"stake":{"lane":"Safe","amount":50},"assets":50,"bonuses":0,"liabilities":0,"portfolio":90},"""
            + """{"seat":2,"tile":5,"mon":0,"assets":0,"bonuses":0,"liabilities":6,"portfolio":-6},"""
            + """{"seat":3,"tile":0,"mon":100,"assets":0,"bonuses":0,"liabilities":0,"portfolio":100}]""",
            views[0].GetProperty("pawns").GetRawText());
        Assert.Equal(50, views[0].GetProperty("forkStake").GetInt32());
        Assert.Equal(
            """[{"lane":"Safe","outcomes":[{"chance":100,"percent":125}]},{"lane":"Degen","outcomes":[{"chance":30,"percent":300},{"chance":70,"percent":20}]}]""",
            views[0].GetProperty("stakeReturns").GetRawText());
    }

    [Fact]
    public void Rules_without_a_fork_stake_offer_no_stake()
    {
        var noStakes = LifeRules.Create(
            2,
            LifeRulesV1.Rules.Tiles,
            startingMon: 100,
            finishBonus: 100,
            timeCap: TimeSpan.FromMinutes(20),
            dieFaces: 6,
            events: LifeRulesV1.Rules.Events.Values);
        var rules = new ChogLifeMatchRules([noStakes]);
        var (room, players) = Started(2, rules);
        using var state = JsonDocument.Parse(
            """{"rulesVersion":2,"pawns":[{"seat":1,"tile":9,"mon":100},{"seat":2,"tile":0,"mon":100}],"choosing":1}""");

        var chosen = rules.Apply(new MatchCommandContext(
            room, players[0].Id, ChogLifeMatchRules.ChooseLane, JsonSerializer.SerializeToElement(new { lane = "Degen" }), state.RootElement, new Dice()));
        var view = rules.ViewFor(new MatchViewContext(room, players[0].Id, chosen.State!.Value))!.Value;

        Assert.Equal("""{"seat":1,"tile":9,"mon":100,"lane":"Degen"}""", Pawn(chosen).GetRawText());
        Assert.False(view.TryGetProperty("forkStake", out _));
        Assert.False(view.TryGetProperty("stakeReturns", out _));
    }

    [Theory]
    [InlineData(""","liabilities":-1""")]
    [InlineData(""","liabilities":1000001""")]
    [InlineData(""","bonuses":-1""")]
    [InlineData(""","stake":{"lane":"Safe","amount":0}""")]
    [InlineData(""","stake":{"lane":"Safe","amount":1000001}""")]
    [InlineData(""","stake":{"lane":"Main","amount":50}""")]
    [InlineData(""","stake":{"lane":"Sideways","amount":50}""")]
    public void A_roll_or_choice_with_an_invalid_portfolio_is_rejected_without_a_draw(string extra)
    {
        var (room, players) = Started(2);
        var state = $$"""{"rulesVersion":1,"pawns":[{"seat":1,"tile":9,"mon":100{{extra}}},{"seat":2,"tile":0,"mon":100}]}""";
        var choosing = state.Replace("]}", "],\"choosing\":1}");
        var dice = new Dice();

        Assert.Equal("game_state_unavailable", Roll(room, players[0], state, dice).Reason);
        Assert.Equal("game_state_unavailable", Choose(room, players[0], choosing, new { lane = "Safe" }).Reason);
        Assert.Null(TimeOut(room, choosing));
        Assert.Empty(dice.Draws);
    }
}
