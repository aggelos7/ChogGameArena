using ChogGames.Games.ChogLife.Rules;

namespace ChogGames.Games.ChogLife.Tests.Rules;

/// <summary>STORY-044, DEC-047: version 2 is version 1 with a 50 MON first-finish bonus.</summary>
public class LifeRulesV2Tests
{
    private static readonly LifeRules V1 = LifeRulesV1.Rules;
    private static readonly LifeRules V2 = LifeRulesV2.Rules;

    [Fact]
    public void Version_2_halves_the_finish_bonus()
    {
        Assert.Equal(("chog-life", 2), (V2.GameType, V2.Version));
        Assert.Equal(50, V2.FinishBonus);
        Assert.Equal(100, V1.FinishBonus);
    }

    [Fact]
    public void Everything_else_is_version_1()
    {
        Assert.Equal(V1.Tiles, V2.Tiles);
        Assert.Equal(
            (V1.StartingMon, V1.TimeCap, V1.DieFaces, V1.DefaultLane, V1.ForkStake),
            (V2.StartingMon, V2.TimeCap, V2.DieFaces, V2.DefaultLane, V2.ForkStake));
        Assert.Equal(V1.Events, V2.Events);
        Assert.Equal(V1.Stakes, V2.Stakes);
    }
}
