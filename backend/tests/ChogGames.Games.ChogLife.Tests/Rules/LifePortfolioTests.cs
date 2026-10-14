using ChogGames.Games.ChogLife.Rules;

namespace ChogGames.Games.ChogLife.Tests.Rules;

/// <summary>
/// STORY-040, DEC-042 (LIFE-006, LIFE-011): a portfolio is liquid + assets + bonuses − liabilities. A gain repays
/// liabilities first; a loss takes liquid MON and leaves what it can't take as a liability. Liquid MON and liabilities
/// are capped, so nothing overflows.
/// </summary>
public class LifePortfolioTests
{
    [Fact]
    public void The_total_is_liquid_plus_assets_plus_bonuses_minus_liabilities()
    {
        Assert.Equal(100, new LifePortfolio(100).Total);
        Assert.Equal(40 + 50 + 100 - 0, new LifePortfolio(40, 50, 100).Total);
        Assert.Equal(-6, new LifePortfolio(0, Liabilities: 6).Total);
        Assert.Equal(3 * LifeRules.MaxMon, new LifePortfolio(LifeRules.MaxMon, LifeRules.MaxMon, LifeRules.MaxMon).Total);
    }

    [Theory]
    [InlineData(100, 0, -15, 85, 0)]
    [InlineData(9, 0, -15, 0, 6)]
    [InlineData(0, 0, -20, 0, 20)]
    [InlineData(0, 20, -20, 0, 40)]
    [InlineData(100, 0, 0, 100, 0)]
    [InlineData(100, 0, 20, 120, 0)]
    [InlineData(0, 6, 20, 14, 0)]
    [InlineData(0, 30, 20, 0, 10)]
    [InlineData(0, 20, 20, 0, 0)]
    public void A_loss_takes_liquid_mon_first_and_a_gain_repays_liabilities_first(
        int liquid, int liabilities, int change, int liquidAfter, int liabilitiesAfter)
    {
        var settled = new LifePortfolio(liquid, 50, 10, liabilities).Settle(change);

        Assert.Equal(new LifePortfolio(liquidAfter, 50, 10, liabilitiesAfter), settled);
        Assert.Equal(change, settled.Total - new LifePortfolio(liquid, 50, 10, liabilities).Total);
    }

    [Fact]
    public void Liquid_mon_and_liabilities_never_rise_above_the_cap()
    {
        Assert.Equal(LifeRules.MaxMon, new LifePortfolio(LifeRules.MaxMon - 5).Settle(20).Liquid);
        Assert.Equal(LifeRules.MaxMon, new LifePortfolio(0, Liabilities: LifeRules.MaxMon - 5).Settle(-20).Liabilities);
        Assert.Equal(LifeRules.MaxMon, new LifePortfolio(0).Settle(int.MinValue).Liabilities);
        Assert.Equal(LifeRules.MaxMon, new LifePortfolio(LifeRules.MaxMon).Settle(int.MaxValue).Liquid);
    }

    [Theory]
    [InlineData(-1, 0, 0, 0)]
    [InlineData(0, -1, 0, 0)]
    [InlineData(0, 0, -1, 0)]
    [InlineData(0, 0, 0, -1)]
    [InlineData(LifeRules.MaxMon + 1, 0, 0, 0)]
    [InlineData(0, 0, 0, LifeRules.MaxMon + 1)]
    public void Every_part_must_be_within_range(int liquid, int assets, int bonuses, int liabilities)
    {
        Assert.False(new LifePortfolio(liquid, assets, bonuses, liabilities).IsValid);
        Assert.True(new LifePortfolio(LifeRules.MaxMon, LifeRules.MaxMon, LifeRules.MaxMon, LifeRules.MaxMon).IsValid);
    }
}
