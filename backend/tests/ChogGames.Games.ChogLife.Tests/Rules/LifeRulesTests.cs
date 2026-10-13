using ChogGames.Games.ChogLife.Rules;
using static ChogGames.Games.ChogLife.Rules.LifeLane;
using static ChogGames.Games.ChogLife.Rules.LifeTile;

namespace ChogGames.Games.ChogLife.Tests.Rules;

/// <summary>
/// STORY-035, DEC-037: rules are checked when built. Every malformed board or value is refused with an
/// <see cref="ArgumentException"/>, so it can never reach a match.
/// </summary>
public class LifeRulesTests
{
    /// <summary>A valid board to break one tile at a time: version 1's.</summary>
    private static LifeTile[] Board() => [.. LifeRulesV1.Rules.Tiles];

    private static LifeTile[] Board(int index, Func<LifeTile, LifeTile> change)
    {
        var board = Board();
        board[index] = change(board[index]);
        return board;
    }

    /// <summary>Start, <paramref name="count"/> - 2 normal tiles in a row, Finish.</summary>
    private static IEnumerable<LifeTile> Linear(int count) =>
        Enumerable.Range(0, count).Select(i => i switch
        {
            0 => Start(0, 1),
            _ when i == count - 1 => Finish(i),
            _ => Normal(i, Main, i + 1),
        });

    private static LifeRules Build(
        IEnumerable<LifeTile>? tiles = null,
        int version = 1,
        int startingMon = 100,
        int finishBonus = 100,
        TimeSpan? timeCap = null,
        int dieFaces = 6,
        IEnumerable<LifeEvent>? events = null) =>
        LifeRules.Create(
            version, tiles ?? Board(), startingMon, finishBonus, timeCap ?? TimeSpan.FromMinutes(20), dieFaces, events ?? LifeRulesV1.Rules.Events.Values);

    private static string Refused(IEnumerable<LifeTile> tiles) => Assert.ThrowsAny<ArgumentException>(() => Build(tiles)).Message;

    [Theory]
    [InlineData(30)]
    [InlineData(40)]
    public void A_straight_board_of_30_to_40_tiles_is_valid(int count)
    {
        var rules = Build(Linear(count), version: 2, startingMon: 1, finishBonus: 0, timeCap: TimeSpan.FromSeconds(1), dieFaces: 2);

        Assert.Equal((count, 2), (rules.Tiles.Count, rules.Version));
        Assert.Equal((1, 0, TimeSpan.FromSeconds(1), 2), (rules.StartingMon, rules.FinishBonus, rules.TimeCap, rules.DieFaces));
    }

    [Fact]
    public void The_highest_allowed_values_are_valid()
    {
        var rules = Build(Board(2, tile => tile with { Amount = LifeRules.MaxTileAmount }),
            startingMon: LifeRules.MaxStartingMon, finishBonus: LifeRules.MaxStartingMon, timeCap: LifeRules.MaxTimeCap, dieFaces: LifeRules.MaxDieFaces);

        Assert.Equal(LifeRules.MaxTileAmount, rules.Tiles[2].Amount);
    }

    [Theory]
    [InlineData(29)]
    [InlineData(41)]
    [InlineData(0)]
    public void A_board_outside_30_to_40_tiles_is_refused(int count)
    {
        Assert.Contains("needs 30 to 40", Refused(count == 0 ? [] : Linear(count)));
    }

    [Fact]
    public void Missing_tiles_are_refused()
    {
        Assert.Throws<ArgumentNullException>(() => LifeRules.Create(1, null!, 100, 100, TimeSpan.FromMinutes(20), 6, LifeRulesV1.Rules.Events.Values));
        Assert.Contains("missing", Refused(Board(4, _ => null!)));
    }

    [Theory]
    [InlineData(0, 100, 100, 1200, 6, "version")]
    [InlineData(1, 0, 100, 1200, 6, "startingMon")]
    [InlineData(1, 10_001, 100, 1200, 6, "startingMon")]
    [InlineData(1, 100, -1, 1200, 6, "finishBonus")]
    [InlineData(1, 100, 10_001, 1200, 6, "finishBonus")]
    [InlineData(1, 100, 100, 0, 6, "timeCap")]
    [InlineData(1, 100, 100, -1, 6, "timeCap")]
    [InlineData(1, 100, 100, 1201, 6, "timeCap")]
    [InlineData(1, 100, 100, 1200, 1, "dieFaces")]
    [InlineData(1, 100, 100, 1200, 13, "dieFaces")]
    public void Values_out_of_range_are_refused(int version, int startingMon, int finishBonus, int timeCapSeconds, int dieFaces, string parameter)
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() =>
            Build(version: version, startingMon: startingMon, finishBonus: finishBonus, timeCap: TimeSpan.FromSeconds(timeCapSeconds), dieFaces: dieFaces));

        Assert.Equal(parameter, error.ParamName);
    }

    [Fact]
    public void Tiles_out_of_order_are_refused()
    {
        var board = Board();
        (board[4], board[5]) = (board[5], board[4]);

        Assert.Contains("index order", Refused(board));
    }

    [Fact]
    public void Unknown_types_lanes_and_event_kinds_are_refused()
    {
        Assert.Contains("unknown type", Refused(Board(1, tile => tile with { Type = (LifeTileType)99 })));
        Assert.Contains("unknown type", Refused(Board(1, tile => tile with { Lane = (LifeLane)99 })));
        Assert.Contains("known event kind", Refused(Board(3, tile => tile with { Event = (LifeEventKind)99 })));
    }

    [Fact]
    public void The_board_must_start_with_the_only_start_and_end_with_the_only_finish()
    {
        Assert.Contains("only Start", Refused(Board(0, _ => Normal(0, Main, 1))));
        Assert.Contains("only Start", Refused(Board(35, _ => Normal(35, Main, 36))));
        Assert.Contains("only Start", Refused(Board(1, _ => Start(1, 2))));
        Assert.Contains("only Start", Refused(Board(1, _ => Finish(1))));
    }

    [Fact]
    public void Starts_finishes_and_branches_must_be_on_the_main_track()
    {
        Assert.Contains("main track", Refused(Board(0, tile => tile with { Lane = Safe })));
        Assert.Contains("main track", Refused(Board(35, tile => tile with { Lane = Degen })));
        Assert.Contains("main track", Refused(Board(9, tile => tile with { Lane = Safe })));
    }

    [Fact]
    public void Each_tile_type_has_its_own_links()
    {
        Assert.Contains("links to nothing", Refused(Board(35, tile => tile with { Next = 35 })));
        Assert.Contains("only to its safe and degen", Refused(Board(9, tile => tile with { Next = 10 })));
        Assert.Contains("only to its safe and degen", Refused(Board(9, tile => tile with { DegenNext = null })));
        Assert.Contains("exactly one next", Refused(Board(1, tile => tile with { Next = null })));
        Assert.Contains("exactly one next", Refused(Board(1, tile => tile with { SafeNext = 10 })));
    }

    [Theory]
    [InlineData(5, 3)]
    [InlineData(5, 5)]
    [InlineData(5, 36)]
    [InlineData(5, -1)]
    public void Links_must_point_forward_to_a_tile_on_the_board(int index, int next)
    {
        Assert.Contains("point forward", Refused(Board(index, tile => tile with { Next = next })));
    }

    [Fact]
    public void Branch_lanes_must_point_forward_too()
    {
        Assert.Contains("point forward", Refused(Board(25, tile => tile with { SafeNext = 10 })));
    }

    [Fact]
    public void Every_tile_must_be_reachable()
    {
        Assert.Contains("Tile 2 can't be reached", Refused(Board(1, tile => tile with { Next = 3 })));
    }

    [Fact]
    public void Only_a_branch_enters_a_lane()
    {
        Assert.Contains("only a Branch enters a lane", Refused(Board(8, tile => tile with { Next = 10 })));
        Assert.Contains("only a Branch enters a lane", Refused(Board(13, tile => tile with { Next = 15 })));
    }

    [Fact]
    public void A_branch_must_lead_into_its_safe_and_degen_lanes()
    {
        Assert.Contains("as its Safe lane", Refused(Board(9, tile => tile with { SafeNext = 14, DegenNext = 10 })));
        Assert.Contains("as its Degen lane", Refused(Board(9, tile => tile with { DegenNext = 18 })));
    }

    [Fact]
    public void Both_lanes_of_a_fork_must_join_the_main_track_at_one_tile()
    {
        Assert.Contains("must join at one", Refused(Board(13, tile => tile with { Next = 19 })));
    }

    [Theory]
    [InlineData(2, 0)]
    [InlineData(2, -5)]
    [InlineData(5, LifeRules.MaxTileAmount + 1)]
    public void Reward_and_loss_tiles_need_an_amount_in_range(int index, int amount)
    {
        Assert.Contains("needs an amount", Refused(Board(index, tile => tile with { Amount = amount })));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(9)]
    [InlineData(35)]
    public void Other_tiles_have_no_amount(int index)
    {
        Assert.Contains("has no amount", Refused(Board(index, tile => tile with { Amount = 10 })));
    }

    [Fact]
    public void Only_event_tiles_have_an_event_kind()
    {
        Assert.Contains("needs a known event kind", Refused(Board(3, tile => tile with { Event = null })));
        Assert.Contains("has no event", Refused(Board(2, tile => tile with { Event = LifeEventKind.Rug })));
    }

    [Fact]
    public void A_refused_board_names_its_tiles_parameter()
    {
        var error = Assert.ThrowsAny<ArgumentException>(() => Build(Board(1, tile => tile with { Next = 3 })));

        Assert.Equal("tiles", error.ParamName);
    }
}
