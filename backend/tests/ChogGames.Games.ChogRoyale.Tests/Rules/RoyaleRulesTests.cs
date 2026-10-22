using ChogGames.Games.ChogRoyale.Rules;

namespace ChogGames.Games.ChogRoyale.Tests.Rules;

/// <summary>
/// Chog Royale's versioned rules, checked when built (STORY-071, STORY-073, STORY-075–077, DEC-082, DEC-084, DEC-086–088).
/// </summary>
public class RoyaleRulesTests
{
    private static readonly RoyaleTiming Timing = RoyaleRulesV1.Rules.Timing;

    private static readonly Dictionary<RoyaleCard, int> Deck = new(RoyaleRulesV1.Rules.Deck);

    private static readonly RoyaleCardEffects Effects = RoyaleRulesV1.Rules.Effects;

    private static readonly RoyaleMarket Market = RoyaleRulesV1.Rules.Market;

    private static Dictionary<RoyaleMarketEvent, int> Events(params (RoyaleMarketEvent Event, int? Weight)[] changes)
    {
        var events = new Dictionary<RoyaleMarketEvent, int>(Market.Events);
        foreach (var (marketEvent, weight) in changes)
        {
            if (weight is { } value)
            {
                events[marketEvent] = value;
            }
            else
            {
                events.Remove(marketEvent);
            }
        }

        return events;
    }

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
        Assert.Equal(new RoyaleCardEffects(20, 15, 5, 20, 20, 5, 25), rules.Effects);
        Assert.Equal(
            [(RoyaleMarketEvent.CalmMarket, 30), (RoyaleMarketEvent.MemeHype, 20), (RoyaleMarketEvent.MarketCrash, 20), (RoyaleMarketEvent.WhaleIncoming, 15), (RoyaleMarketEvent.CommunityAirdrop, 15)],
            Enum.GetValues<RoyaleMarketEvent>().Select(e => (e, rules.Market.Events[e])));
        Assert.Equal(
            [(RoyaleMarketEvent.MemeHype, 1), (RoyaleMarketEvent.MarketCrash, 1), (RoyaleMarketEvent.WhaleIncoming, 1), (RoyaleMarketEvent.CommunityAirdrop, 1)],
            Enum.GetValues<RoyaleMarketEvent>().Where(rules.Market.Chaos.ContainsKey).Select(e => (e, rules.Market.Chaos[e])));
        Assert.Equal((150, 50, 5, 5, 100), (rules.Market.HypePercent, rules.Market.CrashPercent, rules.Market.WhaleBonus, rules.Market.CommunityAirdrop, rules.Market.EventWeight));
    }

    [Theory]
    [InlineData(0, RoyaleMarketEvent.CalmMarket)]
    [InlineData(29, RoyaleMarketEvent.CalmMarket)]
    [InlineData(30, RoyaleMarketEvent.MemeHype)]
    [InlineData(50, RoyaleMarketEvent.MarketCrash)]
    [InlineData(70, RoyaleMarketEvent.WhaleIncoming)]
    [InlineData(84, RoyaleMarketEvent.WhaleIncoming)]
    [InlineData(85, RoyaleMarketEvent.CommunityAirdrop)]
    [InlineData(99, RoyaleMarketEvent.CommunityAirdrop)]
    public void An_event_draw_gives_the_event_whose_weight_it_falls_in(int roll, RoyaleMarketEvent marketEvent)
    {
        Assert.Equal(marketEvent, RoyaleRulesV1.Rules.Market.EventAt(roll));
    }

    [Theory]
    [InlineData(RoyaleMarketEvent.CalmMarket, 4, new[] { RoyaleMarketEvent.MemeHype, RoyaleMarketEvent.MarketCrash, RoyaleMarketEvent.WhaleIncoming, RoyaleMarketEvent.CommunityAirdrop })]
    [InlineData(RoyaleMarketEvent.MemeHype, 3, new[] { RoyaleMarketEvent.MarketCrash, RoyaleMarketEvent.WhaleIncoming, RoyaleMarketEvent.CommunityAirdrop })]
    [InlineData(RoyaleMarketEvent.CommunityAirdrop, 3, new[] { RoyaleMarketEvent.MemeHype, RoyaleMarketEvent.MarketCrash, RoyaleMarketEvent.WhaleIncoming })]
    public void Chaos_draws_any_modifier_but_calm_market_and_the_rounds_own_event(RoyaleMarketEvent roundEvent, int weight, RoyaleMarketEvent[] pool)
    {
        var market = RoyaleRulesV1.Rules.Market;

        Assert.Equal(weight, market.ChaosWeight(roundEvent));
        Assert.Equal(pool, Enumerable.Range(0, weight).Select(roll => market.ChaosAt(roundEvent, roll)));
        Assert.Throws<ArgumentOutOfRangeException>(() => market.ChaosAt(roundEvent, weight));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(100)]
    public void An_event_draw_outside_the_total_weight_is_refused(int roll)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => RoyaleRulesV1.Rules.Market.EventAt(roll));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(4, 0)]
    [InlineData(9, 1)]
    [InlineData(54, 10)]
    [InlineData(100, 20)]
    [InlineData(101, 20)]
    [InlineData(10_000, 20)]
    public void A_whale_swap_gives_a_fifth_of_the_balance_rounded_down_and_at_most_20(int snapshot, int gives)
    {
        Assert.Equal(gives, RoyaleRulesV1.Rules.Effects.SwapAmount(snapshot));
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
        int handSize = 3,
        RoyaleCardEffects? effects = null,
        RoyaleMarket? market = null) =>
        RoyaleRules.Create(
            version, rounds, timing ?? Timing, timeCap ?? TimeSpan.FromMinutes(5), startingMon, maxMon, deck ?? Deck, handSize, effects ?? Effects, market ?? Market);

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
        { "no pump gain", () => Create(effects: Effects with { Pump = 0 }) },
        { "big rug pull", () => Create(effects: Effects with { RugPull = 101 }) },
        { "negative diamond hands", () => Create(effects: Effects with { DiamondHands = -1 }) },
        { "swap over 100%", () => Create(effects: Effects with { SwapPercent = 101 }) },
        { "no swap cap", () => Create(effects: Effects with { SwapCap = 0 }) },
        { "airdrop range reversed", () => Create(effects: Effects with { AirdropMin = 26 }) },
        { "big airdrop", () => Create(effects: Effects with { AirdropMax = 101 }) },
        { "missing event", () => Create(market: Market with { Events = Events((RoyaleMarketEvent.CalmMarket, null)) }) },
        { "weightless event", () => Create(market: Market with { Events = Events((RoyaleMarketEvent.MemeHype, 0)) }) },
        { "unknown event", () => Create(market: Market with { Events = Events(((RoyaleMarketEvent)42, 5)) }) },
        { "calm market in chaos", () => Create(market: Market with { Chaos = new Dictionary<RoyaleMarketEvent, int>(Market.Chaos) { [RoyaleMarketEvent.CalmMarket] = 1 } }) },
        { "missing chaos modifier", () => Create(market: Market with { Chaos = Market.Chaos.Where(c => c.Key != RoyaleMarketEvent.MemeHype).ToDictionary() }) },
        { "weightless chaos modifier", () => Create(market: Market with { Chaos = new Dictionary<RoyaleMarketEvent, int>(Market.Chaos) { [RoyaleMarketEvent.MemeHype] = 0 } }) },
        { "hype that lowers pump", () => Create(market: Market with { HypePercent = 99 }) },
        { "hype over 300%", () => Create(market: Market with { HypePercent = 301 }) },
        { "crash that raises pump", () => Create(market: Market with { CrashPercent = 101 }) },
        { "negative whale bonus", () => Create(market: Market with { WhaleBonus = -1 }) },
        { "big community airdrop", () => Create(market: Market with { CommunityAirdrop = 101 }) },
    };

    [Theory]
    [MemberData(nameof(Invalid))]
    public void Invalid_rules_are_refused(string why, Func<RoyaleRules> create)
    {
        Assert.NotNull(why);
        Assert.ThrowsAny<ArgumentException>(create);
    }
}
