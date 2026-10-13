using ChogGames.Games.ChogLife.Rules;

namespace ChogGames.Games.ChogLife.Tests.Rules;

/// <summary>STORY-035, DEC-037: the shipped rules have the agreed values and a board every route can finish.</summary>
public class LifeRulesV1Tests
{
    private static readonly LifeRules Rules = LifeRulesV1.Rules;

    [Fact]
    public void Version_1_has_the_agreed_values()
    {
        Assert.Equal(("chog-life", 1), (Rules.GameType, Rules.Version));
        Assert.Equal(36, Rules.Tiles.Count);
        Assert.Equal(100, Rules.StartingMon);
        Assert.Equal(100, Rules.FinishBonus);
        Assert.Equal(TimeSpan.FromMinutes(20), Rules.TimeCap);
        Assert.Equal(6, Rules.DieFaces);
        Assert.Equal((LifeTileType.Start, LifeTileType.Finish), (Rules.Start.Type, Rules.Finish.Type));
    }

    [Fact]
    public void Board_has_two_safe_degen_forks()
    {
        var branches = Rules.Tiles.Where(tile => tile.Type == LifeTileType.Branch).ToArray();

        Assert.Equal([9, 25], branches.Select(branch => branch.Index));
        Assert.All(branches, branch =>
        {
            Assert.Equal(LifeLane.Safe, Rules.Tiles[branch.SafeNext!.Value].Lane);
            Assert.Equal(LifeLane.Degen, Rules.Tiles[branch.DegenNext!.Value].Lane);
        });
    }

    [Fact]
    public void Every_route_reaches_the_finish_over_the_same_number_of_tiles()
    {
        var routes = Routes(Rules.Start).ToArray();

        Assert.Equal(4, routes.Length);
        Assert.All(routes, route => Assert.Equal(Rules.Finish.Index, route[^1]));
        Assert.Single(routes.Select(route => route.Count).Distinct());
        Assert.Equal(Rules.Tiles.Select(tile => tile.Index), routes.SelectMany(route => route).Distinct().Order());
    }

    [Fact]
    public void Every_tile_has_its_amount_or_event_and_nothing_else()
    {
        foreach (var tile in Rules.Tiles)
        {
            Assert.Equal(tile.Type is LifeTileType.Reward or LifeTileType.Loss, tile.Amount > 0);
            Assert.Equal(tile.Type == LifeTileType.Event, tile.Event.HasValue);
        }

        Assert.Equal(
            Enum.GetValues<LifeEventKind>(),
            Rules.Tiles.Where(tile => tile.Event.HasValue).Select(tile => tile.Event!.Value).Distinct().Order());
    }

    private static IEnumerable<List<int>> Routes(LifeTile from)
    {
        var successors = from.Successors.ToArray();
        if (successors.Length == 0)
        {
            yield return [from.Index];
            yield break;
        }

        foreach (var next in successors)
        {
            foreach (var rest in Routes(Rules.Tiles[next]))
            {
                rest.Insert(0, from.Index);
                yield return rest;
            }
        }
    }
}
