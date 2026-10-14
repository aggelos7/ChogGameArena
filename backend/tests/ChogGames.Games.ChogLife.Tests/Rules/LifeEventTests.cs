using ChogGames.Core.Matches;
using ChogGames.Games.ChogLife.Rules;
using static ChogGames.Games.ChogLife.Rules.LifeEventOutcome;

namespace ChogGames.Games.ChogLife.Tests.Rules;

/// <summary>
/// STORY-038, DEC-040: the tile a pawn stops on resolves. Reward and Loss tiles move their MON, each event draws one of
/// its configured outcomes from the injected source of chance. A loss is the full amount even when it's more than the
/// liquid MON held: settling it turns the rest into a liability (STORY-040, DEC-042). Malformed event definitions are
/// refused when the rules are built.
/// </summary>
public class LifeEventTests
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

    private static int? Resolve(int tile, int liquid, params int[] draws) => Rules.Resolve(tile, liquid, new Chance(draws));

    private const int Reward20 = 2;
    private const int Airdrop = 3;
    private const int Loss15 = 5;
    private const int Market = 6;
    private const int Rug = 14;
    private const int Liquidation = 16;

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(9)]
    [InlineData(35)]
    public void Start_normal_branch_and_finish_tiles_do_nothing_and_draw_nothing(int tile)
    {
        var chance = new Chance();

        Assert.Null(Rules.Resolve(tile, 100, chance));
        Assert.Empty(chance.Draws);
    }

    [Theory]
    [InlineData(Reward20, 100, 20)]
    [InlineData(Reward20, 0, 20)]
    [InlineData(Loss15, 100, -15)]
    [InlineData(Loss15, 10, -15)]
    [InlineData(Loss15, 0, -15)]
    public void Reward_and_loss_tiles_move_their_amount_without_a_draw(int tile, int liquid, int change)
    {
        var chance = new Chance();

        Assert.Equal(change, Rules.Resolve(tile, liquid, chance));
        Assert.Empty(chance.Draws);
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(59, 10)]
    [InlineData(60, 25)]
    [InlineData(89, 25)]
    [InlineData(90, 50)]
    [InlineData(99, 50)]
    public void An_airdrop_pays_10_25_or_50_mon_at_60_30_and_10_percent(int draw, int change)
    {
        var chance = new Chance(draw);

        Assert.Equal(change, Rules.Resolve(Airdrop, 100, chance));
        Assert.Equal([(0, 100)], chance.Draws);
    }

    [Theory]
    [InlineData(0, 100, 10)]
    [InlineData(49, 255, 25)]
    [InlineData(0, 30, 5)]
    [InlineData(0, 0, 5)]
    [InlineData(50, 100, -10)]
    [InlineData(99, 255, -25)]
    [InlineData(50, 30, -5)]
    [InlineData(50, 3, -5)]
    [InlineData(50, 0, -5)]
    public void A_market_moves_10_percent_up_or_down_at_least_5_mon(int draw, int liquid, int change)
    {
        Assert.Equal(change, Resolve(Market, liquid, draw));
    }

    [Theory]
    [InlineData(100, -40)]
    [InlineData(99, -39)]
    [InlineData(1, 0)]
    [InlineData(0, 0)]
    public void A_rug_takes_40_percent_rounded_down_without_a_draw(int liquid, int change)
    {
        var chance = new Chance();

        Assert.Equal(change, Rules.Resolve(Rug, liquid, chance));
        Assert.Empty(chance.Draws);
    }

    [Theory]
    [InlineData(200, -50)]
    [InlineData(100, -25)]
    [InlineData(50, -20)]
    [InlineData(10, -20)]
    [InlineData(0, -20)]
    public void A_liquidation_takes_25_percent_but_at_least_20_mon_even_from_less(int liquid, int change)
    {
        Assert.Equal(change, Resolve(Liquidation, liquid));
    }

    [Fact]
    public void A_tile_moves_its_full_amount_whatever_the_balance()
    {
        // The cap and the floor are applied when the change is settled (LifePortfolio.Settle).
        Assert.Equal(20, Resolve(Reward20, LifeRules.MaxMon));
        Assert.Equal(100_000, Resolve(Market, LifeRules.MaxMon, 0));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(LifeRules.MaxMon + 1)]
    public void A_balance_out_of_range_is_refused(int liquid)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Resolve(Reward20, liquid));
    }

    [Fact]
    public void Version_1_defines_every_event_with_chances_adding_up_to_100()
    {
        Assert.Equal(Enum.GetValues<LifeEventKind>(), Rules.Events.Keys.Order());
        Assert.All(Rules.Events.Values, definition => Assert.Equal(100, definition.Outcomes.Sum(outcome => outcome.Chance)));
        Assert.Equal([Gain(60, 10), Gain(30, 25), Gain(10, 50)], Rules.Events[LifeEventKind.Airdrop].Outcomes);
        Assert.Equal([GainPercent(50, 10, 5), LosePercent(50, 10, 5)], Rules.Events[LifeEventKind.Market].Outcomes);
        Assert.Equal([LosePercent(100, 40)], Rules.Events[LifeEventKind.Rug].Outcomes);
        Assert.Equal([LosePercent(100, 25, 20)], Rules.Events[LifeEventKind.Liquidation].Outcomes);
    }

    private static LifeEvent[] Events(LifeEventKind kind, params LifeEventOutcome[] outcomes) =>
        [.. Rules.Events.Values.Select(definition => definition.Kind == kind ? definition with { Outcomes = outcomes } : definition)];

    private static ArgumentException Refused(IEnumerable<LifeEvent> events) => Assert.ThrowsAny<ArgumentException>(() =>
        LifeRules.Create(1, Rules.Tiles, 100, 100, TimeSpan.FromMinutes(20), 6, events));

    [Fact]
    public void Events_are_required()
    {
        Assert.Throws<ArgumentNullException>(() => LifeRules.Create(1, Rules.Tiles, 100, 100, TimeSpan.FromMinutes(20), 6, null!));
    }

    [Fact]
    public void Every_event_kind_on_the_board_must_be_defined_once()
    {
        Assert.Contains("isn't defined", Refused(Rules.Events.Values.Where(definition => definition.Kind != LifeEventKind.Rug)).Message);
        Assert.Contains("defined twice", Refused([.. Rules.Events.Values, Rules.Events[LifeEventKind.Rug]]).Message);
        Assert.Contains("unknown kind", Refused([.. Rules.Events.Values, new LifeEvent((LifeEventKind)99, [Gain(100, 1)])]).Message);
        Assert.Contains("unknown kind", Refused([.. Rules.Events.Values, null!]).Message);
    }

    [Fact]
    public void An_event_needs_outcomes_whose_chances_add_up_to_100()
    {
        Assert.Contains("needs its outcomes", Refused(Events(LifeEventKind.Rug)).Message);
        Assert.Contains("needs its outcomes", Refused(Events(LifeEventKind.Rug, null!)).Message);
        Assert.Contains("add up to 100%", Refused(Events(LifeEventKind.Airdrop, Gain(60, 10), Gain(30, 25))).Message);
        Assert.Contains("add up to 100%", Refused(Events(LifeEventKind.Airdrop, Gain(60, 10), Gain(50, 25))).Message);
        Assert.Contains("chance of 1 to 100%", Refused(Events(LifeEventKind.Airdrop, Gain(100, 10), Gain(0, 25))).Message);
        Assert.Contains("chance of 1 to 100%", Refused(Events(LifeEventKind.Rug, Gain(101, 10))).Message);
    }

    [Theory]
    [InlineData(0, 0, 0, 0, "must move some MON")]
    [InlineData(99, 0, 0, 0, "known effect")]
    [InlineData(0, -1, 0, 0, "moves 0 to")]
    [InlineData(0, LifeRules.MaxTileAmount + 1, 0, 0, "moves 0 to")]
    [InlineData(0, 0, 101, 0, "moves 0 to")]
    [InlineData(0, 0, -1, 0, "moves 0 to")]
    [InlineData(0, 0, 0, -1, "moves 0 to")]
    [InlineData(0, 0, 0, LifeRules.MaxTileAmount + 1, "moves 0 to")]
    public void An_outcome_must_move_some_mon_within_range(int effect, int amount, int percent, int minimum, string problem)
    {
        var error = Refused(Events(LifeEventKind.Rug, new LifeEventOutcome(100, (LifeEffect)effect, amount, percent, minimum)));

        Assert.Contains(problem, error.Message);
        Assert.Equal("events", error.ParamName);
    }

    [Fact]
    public void The_highest_allowed_outcome_values_are_valid()
    {
        var rules = LifeRules.Create(1, Rules.Tiles, 100, 100, TimeSpan.FromMinutes(20), 6,
            Events(LifeEventKind.Rug, new LifeEventOutcome(100, LifeEffect.Loss, LifeRules.MaxTileAmount, 100, LifeRules.MaxTileAmount)));

        Assert.Equal(-1_100, rules.Resolve(Rug, 100, new Chance()));
    }
}
