using ChogGames.Games.ChogLife.Rules;

namespace ChogGames.Games.ChogLife.Tests.Rules;

/// <summary>
/// STORY-037, DEC-039: a roll moves a pawn forward along the board; it stops on a Branch whatever steps are left, leaves
/// a branch by its safe lane (the choice comes with STORY-039), and stops on the Finish when it overshoots.
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
    public void Leaving_a_branch_takes_the_safe_lane_until_the_choice_exists(int from, int roll, int[] path)
    {
        Assert.Equal(path, Rules.Path(from, roll));
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
                var tile = start;
                for (var turn = 0; turn < Rules.Tiles.Count && tile != Rules.Finish.Index; turn++)
                {
                    tile = Rules.Path(tile, roll)[^1];
                }

                Assert.Equal(Rules.Finish.Index, tile);
            }
        }
    }
}
