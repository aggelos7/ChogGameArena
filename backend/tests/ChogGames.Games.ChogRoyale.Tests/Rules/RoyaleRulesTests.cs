using ChogGames.Games.ChogRoyale.Rules;

namespace ChogGames.Games.ChogRoyale.Tests.Rules;

/// <summary>Chog Royale's versioned rules, checked when built (STORY-071, STORY-073, DEC-082, DEC-084).</summary>
public class RoyaleRulesTests
{
    private static readonly RoyaleTiming Timing = RoyaleRulesV1.Rules.Timing;

    private static readonly Dictionary<RoyaleCard, int> Deck = new(RoyaleRulesV1.Rules.Deck);

    [Fact]
    public void Version_1_has_the_agreed_values()
    {
        var rules = RoyaleRulesV1.Rules;

        Assert.Equal(("chog-royale", 1, 5, 100, 10_000, 3), (rules.GameType, rules.Version, rules.Rounds, rules.StartingMon, rules.MaxMon, rules.HandSize));
        Assert.Equal(
            (TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5)),
            (rules.Timing.Announcement, rules.Timing.Decision, rules.Timing.Reveal, rules.Timing.Transition));
        Assert.Equal(TimeSpan.FromMinutes(5), rules.TimeCap);
        Assert.Equal(
            [(RoyaleCard.Pump, 30), (RoyaleCard.RugPull, 20), (RoyaleCard.DiamondHands, 15), (RoyaleCard.WhaleSwap, 10), (RoyaleCard.Airdrop, 15), (RoyaleCard.Chaos, 10)],
            Enum.GetValues<RoyaleCard>().Select(card => (card, rules.Deck[card])));
        Assert.Equal(100, rules.TotalWeight);
    }

    [Fact]
    public void Version_1_rounds_fit_the_five_minute_target()
    {
        var rules = RoyaleRulesV1.Rules;
        var rounds = rules.Timing.ToRounds();

        Assert.Equal((TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(20)), (rounds.Decision, rounds.FirstOpensAfter, rounds.OpensAfter));
        Assert.Equal(TimeSpan.FromSeconds(175), rules.Timing.Round * rules.Rounds);
        Assert.True(rules.Timing.Round * rules.Rounds <= rules.TimeCap);
    }

    [Theory]
    [InlineData(0, RoyaleCard.Pump)]
    [InlineData(29, RoyaleCard.Pump)]
    [InlineData(30, RoyaleCard.RugPull)]
    [InlineData(49, RoyaleCard.RugPull)]
    [InlineData(50, RoyaleCard.DiamondHands)]
    [InlineData(64, RoyaleCard.DiamondHands)]
    [InlineData(65, RoyaleCard.WhaleSwap)]
    [InlineData(75, RoyaleCard.Airdrop)]
    [InlineData(89, RoyaleCard.Airdrop)]
    [InlineData(90, RoyaleCard.Chaos)]
    [InlineData(99, RoyaleCard.Chaos)]
    public void A_draw_deals_the_card_whose_weight_it_falls_in(int roll, RoyaleCard card)
    {
        Assert.Equal(card, RoyaleRulesV1.Rules.CardAt(roll));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(100)]
    public void A_draw_outside_the_total_weight_is_refused(int roll)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => RoyaleRulesV1.Rules.CardAt(roll));
    }

    [Theory]
    [InlineData(-5, 0)]
    [InlineData(0, 0)]
    [InlineData(250, 250)]
    [InlineData(20_000, 10_000)]
    public void Balances_are_clamped_between_zero_and_the_maximum(long mon, int clamped)
    {
        Assert.Equal(clamped, RoyaleRulesV1.Rules.Clamp(mon));
    }

    [Fact]
    public void Only_rug_pull_and_whale_swap_need_a_target()
    {
        Assert.Equal([RoyaleCard.RugPull, RoyaleCard.WhaleSwap], Enum.GetValues<RoyaleCard>().Where(c => c.IsTargeted()));
    }

    private static RoyaleRules Create(
        int version = 1,
        int rounds = 5,
        RoyaleTiming? timing = null,
        TimeSpan? timeCap = null,
        int startingMon = 100,
        int maxMon = 10_000,
        IReadOnlyDictionary<RoyaleCard, int>? deck = null,
        int handSize = 3) =>
        RoyaleRules.Create(version, rounds, timing ?? Timing, timeCap ?? TimeSpan.FromMinutes(5), startingMon, maxMon, deck ?? Deck, handSize);

    [Fact]
    public void Valid_rules_are_built()
    {
        Assert.Equal(2, Create(version: 2).Version);
    }

    public static TheoryData<string, Func<RoyaleRules>> Invalid => new()
    {
        { "version 0", () => Create(version: 0) },
        { "no rounds", () => Create(rounds: 0) },
        { "11 rounds", () => Create(rounds: 11) },
        { "short decision", () => Create(timing: Timing with { Decision = TimeSpan.FromSeconds(4) }) },
        { "negative reveal", () => Create(timing: Timing with { Reveal = TimeSpan.FromSeconds(-1) }) },
        { "long announcement", () => Create(timing: Timing with { Announcement = TimeSpan.FromMinutes(2) }) },
        { "cap shorter than the rounds", () => Create(timeCap: TimeSpan.FromSeconds(174)) },
        { "cap over 20 minutes", () => Create(rounds: 10, timeCap: TimeSpan.FromMinutes(21)) },
        { "no starting MON", () => Create(startingMon: 0) },
        { "maximum under the start", () => Create(maxMon: 99) },
        { "maximum over the limit", () => Create(maxMon: 10_001) },
        { "missing card", () => Create(deck: Deck.Where(c => c.Key != RoyaleCard.Chaos).ToDictionary()) },
        { "weightless card", () => Create(deck: new Dictionary<RoyaleCard, int>(Deck) { [RoyaleCard.Chaos] = 0 }) },
        { "heavy card", () => Create(deck: new Dictionary<RoyaleCard, int>(Deck) { [RoyaleCard.Pump] = 101 }) },
        { "unknown card", () => Create(deck: new Dictionary<RoyaleCard, int>(Deck.Where(c => c.Key != RoyaleCard.Chaos)) { [(RoyaleCard)42] = 5 }) },
        { "empty hand", () => Create(handSize: 0) },
        { "big hand", () => Create(handSize: 6) },
    };

    [Theory]
    [MemberData(nameof(Invalid))]
    public void Invalid_rules_are_refused(string why, Func<RoyaleRules> create)
    {
        Assert.NotNull(why);
        Assert.ThrowsAny<ArgumentException>(create);
    }
}
