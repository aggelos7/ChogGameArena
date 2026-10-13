using System.Text.Json;
using ChogGames.Core.Games;
using ChogGames.Core.Matches;
using ChogGames.Core.Players;
using ChogGames.Core.Rooms;
using ChogGames.Games.ChogLife.Rules;

namespace ChogGames.Games.ChogLife.Tests;

/// <summary>
/// STORY-036, DEC-038: a Chog Life match starts with every pawn on the start tile under the current rules version, and
/// every player sees that version's board and the pawns of the players still playing. There are no moves yet.
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
            dieFaces: 6);
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

    [Fact]
    public void A_move_stops_on_a_branch_and_the_next_one_takes_the_safe_lane()
    {
        var (room, players) = Started(2);

        var toBranch = Roll(room, players[0], StateWith((1, 6), (2, 0)), new Dice(6));
        Assert.Equal("""{"seat":1,"roll":6,"from":6,"to":9,"path":[7,8,9]}""", LastMove(toBranch).GetRawText());

        var offBranch = Roll(room, players[0], toBranch.State!.Value.GetRawText(), new Dice(2));
        Assert.Equal("""{"seat":1,"roll":2,"from":9,"to":11,"path":[10,11]}""", LastMove(offBranch).GetRawText());
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

        var decision = Roll(room, players[0], null, new Dice(3));

        Assert.Equal([(1, 3), (2, 0)], Pawns(decision.State!.Value));
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

        Assert.Equal(["""{"seat":1,"roll":5,"from":0,"to":5,"path":[1,2,3,4,5]}"""], moves);
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
}
