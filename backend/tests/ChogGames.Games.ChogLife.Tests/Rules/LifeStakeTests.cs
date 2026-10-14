using ChogGames.Core.Matches;
using ChogGames.Games.ChogLife.Rules;

namespace ChogGames.Games.ChogLife.Tests.Rules;

/// <summary>
/// STORY-040, DEC-042 (LIFE-005): the lane taken at a fork holds a stake that pays back a share drawn from that lane's
/// outcomes. Version 1 stakes 50 MON: the safe lane returns 125%, the degen lane 300% at 30% or 20% at 70%. Malformed
/// stakes are refused when the rules are built.
/// </summary>
public class LifeStakeTests
{
    private static readonly LifeRules Rules = LifeRulesV1.Rules;

    /// <summary>Returns the queued numbers in order and records every draw.</summary>
    private sealed class Chance(params int[] values) : IRandomSource
    {
        private readonly Queue<int> _values = new(values);

        public List<(int Min, int Max)> Draws { get; } = [];

        public int Next(int minInclusive, int maxExclusive)
        {
            Draws.Add((minInclusive, maxExclusive));
            return _values.Dequeue();
        }
    }

    [Fact]
    public void Version_1_stakes_50_mon_with_explicit_odds_on_each_lane()
    {
        Assert.Equal(50, Rules.ForkStake);
        Assert.Equal([new LifeStakeOutcome(100, 125)], Rules.Stakes[LifeLane.Safe].Outcomes);
        Assert.Equal([new LifeStakeOutcome(30, 300), new LifeStakeOutcome(70, 20)], Rules.Stakes[LifeLane.Degen].Outcomes);
        Assert.Equal([LifeLane.Safe, LifeLane.Degen], Rules.Stakes.Keys.Order());
    }

    [Fact]
    public void A_safe_stake_pays_back_125_percent_without_a_draw()
    {
        var chance = new Chance();

        Assert.Equal(62, Rules.Payout(new LifeAsset(LifeLane.Safe, 50), chance));
        Assert.Equal(8, Rules.Payout(new LifeAsset(LifeLane.Safe, 7), chance));
        Assert.Empty(chance.Draws);
    }

    [Theory]
    [InlineData(0, 150)]
    [InlineData(29, 150)]
    [InlineData(30, 10)]
    [InlineData(99, 10)]
    public void A_degen_stake_triples_at_30_percent_and_keeps_a_fifth_at_70(int draw, int payout)
    {
        var chance = new Chance(draw);

        Assert.Equal(payout, Rules.Payout(new LifeAsset(LifeLane.Degen, 50), chance));
        Assert.Equal([(0, 100)], chance.Draws);
    }

    [Fact]
    public void A_stake_rules_dont_pay_or_out_of_range_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Rules.Payout(new LifeAsset(LifeLane.Main, 50), new Chance()));
        Assert.Throws<ArgumentOutOfRangeException>(() => Rules.Payout(new LifeAsset(LifeLane.Safe, 0), new Chance()));
        Assert.Throws<ArgumentOutOfRangeException>(() => NoStakes().Payout(new LifeAsset(LifeLane.Safe, 50), new Chance()));
    }

    private static LifeRules NoStakes() =>
        LifeRules.Create(1, Rules.Tiles, 100, 100, TimeSpan.FromMinutes(20), 6, Rules.Events.Values);

    [Fact]
    public void Rules_without_a_fork_stake_have_no_stakes()
    {
        var rules = NoStakes();

        Assert.Equal(0, rules.ForkStake);
        Assert.Empty(rules.Stakes);
    }

    private static LifeStakeOutcome Out(int chance, int percent) => new(chance, percent);

    private static LifeLaneStake Safe(params LifeStakeOutcome[] outcomes) => new(LifeLane.Safe, outcomes);

    private static LifeLaneStake Degen(params LifeStakeOutcome[] outcomes) => new(LifeLane.Degen, outcomes);

    private static readonly LifeLaneStake GoodSafe = Safe(new LifeStakeOutcome(100, 125));
    private static readonly LifeLaneStake GoodDegen = Degen(new LifeStakeOutcome(30, 300), new LifeStakeOutcome(70, 20));

    private static ArgumentException Refused(int forkStake, IEnumerable<LifeLaneStake>? stakes) =>
        Assert.ThrowsAny<ArgumentException>(() =>
            LifeRules.Create(1, Rules.Tiles, 100, 100, TimeSpan.FromMinutes(20), 6, Rules.Events.Values, forkStake: forkStake, stakes: stakes));

    [Theory]
    [InlineData(-1)]
    [InlineData(LifeRules.MaxTileAmount + 1)]
    public void The_fork_stake_must_be_in_range(int forkStake)
    {
        Assert.IsType<ArgumentOutOfRangeException>(Refused(forkStake, [GoodSafe, GoodDegen]));
    }

    [Fact]
    public void A_fork_stake_needs_returns_for_both_lanes_once()
    {
        Assert.Contains("needs the returns", Refused(50, null).Message);
        Assert.Contains("both the Safe and the Degen", Refused(50, [GoodSafe]).Message);
        Assert.Contains("both the Safe and the Degen", Refused(50, []).Message);
        Assert.Contains("defined twice", Refused(50, [GoodSafe, GoodDegen, GoodSafe]).Message);
        Assert.Contains("Safe or the Degen", Refused(50, [GoodSafe, GoodDegen, new LifeLaneStake(LifeLane.Main, [Out(100, 100)])]).Message);
        Assert.Contains("Safe or the Degen", Refused(50, [GoodSafe, GoodDegen, null!]).Message);
        Assert.Equal("stakes", Refused(50, [GoodSafe]).ParamName);
    }

    [Fact]
    public void Each_lane_needs_outcomes_whose_chances_add_up_to_100()
    {
        Assert.Contains("needs its outcomes", Refused(50, [Safe(), GoodDegen]).Message);
        Assert.Contains("needs its outcomes", Refused(50, [Safe(null!), GoodDegen]).Message);
        Assert.Contains("add up to 100%", Refused(50, [GoodSafe, Degen(Out(30, 300), Out(60, 20))]).Message);
        Assert.Contains("chance of 1 to 100%", Refused(50, [GoodSafe, Degen(Out(100, 300), Out(0, 20))]).Message);
        Assert.Contains("chance of 1 to 100%", Refused(50, [Safe(Out(101, 125)), GoodDegen]).Message);
        Assert.Contains("pays back 0 to", Refused(50, [Safe(Out(100, -1)), GoodDegen]).Message);
        Assert.Contains("pays back 0 to", Refused(50, [Safe(Out(100, LifeRules.MaxStakePercent + 1)), GoodDegen]).Message);
    }

    [Fact]
    public void The_highest_allowed_stake_values_are_valid()
    {
        var rules = LifeRules.Create(1, Rules.Tiles, 100, 100, TimeSpan.FromMinutes(20), 6, Rules.Events.Values,
            forkStake: LifeRules.MaxTileAmount,
            stakes: [Safe(Out(100, LifeRules.MaxStakePercent)), Degen(Out(100, 0))]);

        Assert.Equal(10 * LifeRules.MaxMon, rules.Payout(new LifeAsset(LifeLane.Safe, LifeRules.MaxMon), new Chance()));
        Assert.Equal(0, rules.Payout(new LifeAsset(LifeLane.Degen, 50), new Chance()));
    }
}
