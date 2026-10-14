using ChogGames.Games.ChogLife.Rules;

namespace ChogGames.Games.ChogLife.Tests.Rules;

/// <summary>
/// STORY-037, DEC-039: a roll moves a pawn forward along the board; it stops on a Branch whatever steps are left and on
/// the Finish when it overshoots. STORY-039, DEC-041: it leaves a branch by the lane chosen there, or the default lane
/// (Safe in version 1) when none was.
/// </summary>
public class LifeRulesPathTests
{
    private static readonly LifeRules Rules = LifeRulesV1.Rules;

    [Theory]
    [InlineData(0, 1, new[] { 1 })]
    [InlineData(0, 6, new[] { 1, 2, 3, 4, 5, 6 })]
    [InlineData(18, 4, new[] { 19, 20, 21, 22 })]
    [InlineData(28, 3, new[] { 32, 33, 34 })]
    public void A_roll_moves_the_pawn_forward_tile_by_tile(int from, int roll, int[] path)
    {
        Assert.Equal(path, Rules.Path(from, roll));
    }

    [Theory]
    [InlineData(6, 5, new[] { 7, 8, 9 })]
    [InlineData(8, 1, new[] { 9 })]
    [InlineData(22, 6, new[] { 23, 24, 25 })]
    public void A_move_stops_on_a_branch_and_loses_the_steps_left(int from, int roll, int[] path)
    {
        Assert.Equal(path, Rules.Path(from, roll));
    }

    [Theory]
    [InlineData(9, 6, new[] { 10, 11, 12, 13, 18, 19 })]
    [InlineData(25, 4, new[] { 26, 27, 28, 32 })]
    public void Leaving_a_branch_without_a_lane_takes_the_default_safe_lane(int from, int roll, int[] path)
    {
        Assert.Equal(LifeLane.Safe, Rules.DefaultLane);
        Assert.Equal(path, Rules.Path(from, roll));
        Assert.Equal(path, Rules.Path(from, roll, LifeLane.Safe));
    }

    [Theory]
    [InlineData(9, 1, new[] { 14 })]
    [InlineData(9, 6, new[] { 14, 15, 16, 17, 18, 19 })]
    [InlineData(25, 4, new[] { 29, 30, 31, 32 })]
    public void Leaving_a_branch_by_the_degen_lane_follows_it(int from, int roll, int[] path)
    {
        Assert.Equal(path, Rules.Path(from, roll, LifeLane.Degen));
    }

    [Theory]
    [InlineData(4, 3, new[] { 5, 6, 7 })]
    [InlineData(16, 3, new[] { 17, 18, 19 })]
    public void The_lane_only_matters_when_leaving_a_branch(int from, int roll, int[] path)
    {
        Assert.Equal(path, Rules.Path(from, roll, LifeLane.Degen));
        Assert.Equal(path, Rules.Path(from, roll, LifeLane.Safe));
    }

    [Fact]
    public void Leaving_a_branch_by_the_main_lane_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Rules.Path(9, 2, LifeLane.Main));
        Assert.Throws<ArgumentOutOfRangeException>(() => Rules.Path(9, 2, (LifeLane)7));
    }

    [Theory]
    [InlineData(16, 3, new[] { 17, 18, 19 })]
    [InlineData(30, 2, new[] { 31, 32 })]
    public void A_pawn_on_a_degen_lane_follows_it_back_to_the_main_track(int from, int roll, int[] path)
    {
        Assert.Equal(path, Rules.Path(from, roll));
    }

    [Theory]
    [InlineData(33, 6, new[] { 34, 35 })]
    [InlineData(34, 1, new[] { 35 })]
    public void A_roll_past_the_finish_stops_on_it(int from, int roll, int[] path)
    {
        Assert.Equal(path, Rules.Path(from, roll));
    }

    [Fact]
    public void A_pawn_on_the_finish_does_not_move()
    {
        Assert.Empty(Rules.Path(35, 6));
    }

    [Theory]
    [InlineData(-1, 3)]
    [InlineData(36, 3)]
    [InlineData(0, 0)]
    [InlineData(0, -2)]
    public void A_pawn_off_the_board_or_a_move_without_steps_is_refused(int from, int roll)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Rules.Path(from, roll));
    }

    [Fact]
    public void Every_tile_reaches_the_finish_by_rolling()
    {
        // Whatever the rolls, no pawn gets stuck: the lowest and highest rolls both reach the finish from every tile.
        foreach (var start in Rules.Tiles.Where(tile => tile.Type != LifeTileType.Finish).Select(tile => tile.Index))
        {
            foreach (var roll in new[] { 1, Rules.DieFaces })
            {
                foreach (var lane in new[] { LifeLane.Safe, LifeLane.Degen })
                {
                    var tile = start;
                    for (var turn = 0; turn < Rules.Tiles.Count && tile != Rules.Finish.Index; turn++)
                    {
                        tile = Rules.Path(tile, roll, lane)[^1];
                    }

                    Assert.Equal(Rules.Finish.Index, tile);
                }
            }
        }
    }

    [Theory]
    [InlineData(35, 0)]
    [InlineData(34, 1)]
    [InlineData(18, 14)]
    [InlineData(10, 18)]
    [InlineData(14, 18)]
    [InlineData(13, 15)]
    [InlineData(17, 15)]
    [InlineData(9, 19)]
    [InlineData(0, 28)]
    public void Steps_to_the_finish_count_the_shortest_route_and_both_lanes_alike(int tile, int steps) =>
        Assert.Equal(steps, LifeRulesV1.Rules.StepsToFinish(tile));

    [Theory]
    [InlineData(-1)]
    [InlineData(36)]
    public void Steps_to_the_finish_from_off_the_board_are_refused(int tile) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => LifeRulesV1.Rules.StepsToFinish(tile));
}
