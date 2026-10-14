using System.Text.Json;
using ChogGames.Core.Games;
using ChogGames.Core.Matches;
using ChogGames.Core.Players;
using ChogGames.Core.Rooms;
using ChogGames.Games.ChogLife.Rules;

namespace ChogGames.Games.ChogLife.Tests;

/// <summary>
/// STORY-043, LIFE-010, DEC-046: every roll tallies its player's statistics on their pawn (Degen routes taken, Degen
/// stakes cashed with a profit, Airdrops, Rugs and Liquidations landed on, the biggest win in one roll), and when the
/// match ends each player still playing gets a result with their starting and final portfolio, place, statistics and
/// Degen Score, which the engine stores with their rank.
/// </summary>
public class ChogLifeResultsTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    private static readonly ChogLifeMatchRules Rules = new([LifeRulesV1.Rules]);

    private static readonly JsonElement NoPayload = JsonSerializer.SerializeToElement(new { });

    /// <summary>Hands out the queued values; drawing more than queued fails the test.</summary>
    private sealed class Dice(params int[] values) : IRandomSource
    {
        private readonly Queue<int> _values = new(values);

        public int Next(int minInclusive, int maxExclusive) => _values.Dequeue();
    }

    private sealed class KeepOrder : IRandomSource
    {
        public int Next(int minInclusive, int maxExclusive) => maxExclusive - 1;
    }

    private static (GameRoom Room, Player[] Players) Started(int count)
    {
        var players = Enumerable.Range(1, count).Select(n =>
        {
            Assert.True(PlayerDisplayName.TryCreate($"Player {n}", out var name));
            return Player.CreateGuest(name, Now);
        }).ToArray();
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
            startState: started => Rules.StartState(new MatchStartContext(started, random))?.GetRawText());
        return (room, players);
    }

    private static string StateOf(params string[] pawns) => $$"""{"rulesVersion":1,"pawns":[{{string.Join(",", pawns)}}]""";

    private const string Waiting = """{"seat":2,"tile":0,"mon":100}""";

    private static MatchRuleDecision Roll(GameRoom room, Player player, string state, IRandomSource dice)
    {
        using var document = JsonDocument.Parse(state);
        return Rules.Apply(new MatchCommandContext(room, player.Id, ChogLifeMatchRules.Roll, NoPayload, document.RootElement.Clone(), dice));
    }

    /// <summary>The stats stored on seat 1's pawn after the roll, or null for none.</summary>
    private static string? Stats(MatchRuleDecision decision)
    {
        Assert.Equal(MatchRuleOutcome.Accepted, decision.Outcome);
        return decision.State!.Value.GetProperty("pawns")[0].TryGetProperty("stats", out var stats) ? stats.GetRawText() : null;
    }

    private static MatchEnding End(GameRoom room, string state, bool timeUp = true)
    {
        using var document = JsonDocument.Parse(state);
        return Rules.EndMatch(new MatchEndContext(room, document.RootElement.Clone(), timeUp));
    }

    private static string? Result(IReadOnlyList<MatchStanding> standings, Player player) =>
        standings.Single(s => s.PlayerId == player.Id).Result?.GetRawText();

    [Fact]
    public void A_degen_route_whose_stake_triples_counts_the_route_the_win_and_the_profit()
    {
        var (room, players) = Started(2);
        var state = StateOf("""{"seat":1,"tile":9,"mon":50,"lane":"Degen","stake":{"lane":"Degen","amount":50}}""", Waiting) + "}";

        // Roll 5 leaves the fork by tiles 14-17 and rejoins at 18; the stake's draw of 0 triples it (+100 on the 100 held).
        var rolled = Roll(room, players[0], state, new Dice(5, 0));

        Assert.Equal("""{"biggestWin":100,"degenRoutes":1,"degenWins":1}""", Stats(rolled));
    }

    [Fact]
    public void A_degen_stake_that_loses_counts_the_route_but_no_win()
    {
        var (room, players) = Started(2);
        var state = StateOf("""{"seat":1,"tile":9,"mon":50,"lane":"Degen","stake":{"lane":"Degen","amount":50}}""", Waiting) + "}";

        var rolled = Roll(room, players[0], state, new Dice(5, 50));

        Assert.Equal("""{"degenRoutes":1}""", Stats(rolled));
    }

    [Fact]
    public void A_safe_route_is_not_a_degen_route()
    {
        var (room, players) = Started(2);
        var state = StateOf("""{"seat":1,"tile":9,"mon":50,"lane":"Safe","stake":{"lane":"Safe","amount":50}}""", Waiting) + "}";

        // Roll 2 stops on tile 11, a normal tile on the safe lane.
        var rolled = Roll(room, players[0], state, new Dice(2));

        Assert.Null(Stats(rolled));
    }

    [Fact]
    public void Rugs_liquidations_and_airdrops_landed_on_are_counted()
    {
        var (room, players) = Started(2);

        var rugged = Roll(room, players[0], StateOf("""{"seat":1,"tile":9,"mon":50,"lane":"Degen","stake":{"lane":"Degen","amount":50}}""", Waiting) + "}", new Dice(1));
        var liquidated = Roll(room, players[0], StateOf("""{"seat":1,"tile":14,"mon":100}""", Waiting) + "}", new Dice(2));
        var airdropped = Roll(room, players[0], StateOf("""{"seat":1,"tile":0,"mon":100}""", Waiting) + "}", new Dice(3, 95));

        Assert.Equal("""{"rugs":1,"degenRoutes":1}""", Stats(rugged));
        Assert.Equal("""{"liquidations":1}""", Stats(liquidated));
        Assert.Equal("""{"airdrops":1,"airdropMon":50,"biggestWin":50}""", Stats(airdropped));
    }

    [Fact]
    public void Statistics_add_up_and_the_biggest_win_is_the_most_one_roll_added()
    {
        var (room, players) = Started(2);
        var state = StateOf("""{"seat":1,"tile":21,"mon":100,"stats":{"rugs":1,"airdrops":2,"airdropMon":35,"biggestWin":40}}""", Waiting) + "}";

        // Roll 1 lands on tile 22, an airdrop of 10 MON: less than the biggest win so far.
        var rolled = Roll(room, players[0], state, new Dice(1, 0));

        Assert.Equal("""{"airdrops":3,"airdropMon":45,"rugs":1,"biggestWin":40}""", Stats(rolled));
    }

    [Fact]
    public void The_finish_bonus_counts_towards_the_biggest_win()
    {
        var (room, players) = Started(2);

        var arrived = Roll(room, players[0], StateOf("""{"seat":1,"tile":33,"mon":100}""", Waiting) + "}", new Dice(6));

        Assert.Equal("""{"biggestWin":100}""", Stats(arrived));
    }

    [Fact]
    public void A_pass_on_the_finish_tallies_nothing()
    {
        var (room, players) = Started(2);
        var state = StateOf("""{"seat":1,"tile":35,"mon":100,"stats":{"rugs":1}}""", """{"seat":2,"tile":3,"mon":100}""") + ""","finished":[1]}""";

        var passed = Roll(room, players[0], state, new Dice());

        Assert.Equal("""{"rugs":1}""", Stats(passed));
    }

    [Theory]
    [InlineData("""{"rugs":-1}""")]
    [InlineData("""{"biggestWin":1000001}""")]
    [InlineData("""{"degenRoutes":"two"}""")]
    public void Statistics_out_of_range_make_the_state_unplayable(string stats)
    {
        var (room, players) = Started(2);

        var decision = Roll(room, players[0], StateOf($$"""{"seat":1,"tile":0,"mon":100,"stats":{{stats}}}""", Waiting) + "}", new Dice(1));

        Assert.Equal("game_state_unavailable", decision.Reason);
    }

    [Fact]
    public void Statistics_are_not_shown_on_the_board()
    {
        var (room, players) = Started(2);
        using var state = JsonDocument.Parse(StateOf("""{"seat":1,"tile":3,"mon":100,"stats":{"rugs":1}}""", Waiting) + "}");

        var view = Rules.ViewFor(new MatchViewContext(room, players[0].Id, state.RootElement))!.Value;

        Assert.False(view.GetProperty("pawns")[0].TryGetProperty("stats", out _));
    }

    [Fact]
    public void Each_player_still_playing_gets_a_result_with_the_ranking()
    {
        var (room, players) = Started(3);
        room.Leave(players[2].Id, Now.AddMinutes(2));
        var state = StateOf(
            """{"seat":1,"tile":35,"mon":200,"bonuses":100,"stats":{"airdrops":1,"airdropMon":25,"rugs":1,"liquidations":1,"biggestWin":100,"degenRoutes":2,"degenWins":1}}""",
            """{"seat":2,"tile":30,"mon":20,"stake":{"lane":"Degen","amount":50},"liabilities":10}""",
            """{"seat":3,"tile":4,"mon":500}""") + ""","finished":[1]}""";

        var ending = End(room, state);

        Assert.Equal(
            """{"startingPortfolio":100,"portfolio":300,"liquid":200,"assets":0,"bonuses":100,"liabilities":0,"place":1,"airdrops":1,"airdropMon":25,"rugsSurvived":1,"liquidations":1,"biggestWin":100,"degenRoutes":2,"degenWins":1,"degenScore":90,"degenScoreVersion":1}""",
            Result(ending.Standings, players[0]));
        Assert.Equal(
            """{"startingPortfolio":100,"portfolio":60,"liquid":20,"assets":50,"bonuses":0,"liabilities":10,"airdrops":0,"airdropMon":0,"rugsSurvived":0,"liquidations":0,"biggestWin":0,"degenRoutes":0,"degenWins":0,"degenScore":0,"degenScoreVersion":1}""",
            Result(ending.Standings, players[1]));
        Assert.DoesNotContain(ending.Standings, s => s.PlayerId == players[2].Id);
    }

    [Theory]
    [InlineData(0, 0, 0, 0, 0)]
    [InlineData(1, 0, 0, 0, 30)]
    [InlineData(2, 1, 1, 0, 80)]
    [InlineData(1, 0, 0, 1, 40)]
    [InlineData(2, 2, 1, 1, 100)]
    [InlineData(2, 5, 5, 2, 100)]
    public void The_degen_score_is_30_per_degen_route_and_10_per_rug_liquidation_or_degen_win_at_most_100(
        int routes, int rugs, int liquidations, int wins, int score)
    {
        var (room, players) = Started(2);
        var stats = JsonSerializer.Serialize(new { degenRoutes = routes, rugs, liquidations, degenWins = wins });

        var ending = End(room, StateOf($$"""{"seat":1,"tile":20,"mon":100,"stats":{{stats}}}""", Waiting) + "}");

        using var result = JsonDocument.Parse(Result(ending.Standings, players[0])!);
        Assert.Equal(score, result.RootElement.GetProperty("degenScore").GetInt32());
    }

    [Fact]
    public void The_last_roll_that_ends_the_match_comes_with_every_result()
    {
        var (room, players) = Started(2);
        var state = StateOf("""{"seat":1,"tile":35,"mon":250}""", """{"seat":2,"tile":34,"mon":80}""") + ""","finished":[1]}""";

        var ended = Roll(room, players[1], state, new Dice(1));

        Assert.NotNull(ended.Standings);
        Assert.Contains("\"place\":1", Result(ended.Standings, players[0]));
        Assert.Contains("\"place\":2,", Result(ended.Standings, players[1]));
    }

    [Fact]
    public void A_state_this_server_cannot_play_ends_without_results()
    {
        var (room, players) = Started(2);

        var ending = End(room, """{"rulesVersion":99,"pawns":[]}""");

        Assert.All(ending.Standings, standing => Assert.Null(standing.Result));
    }
}
