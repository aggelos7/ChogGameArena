using ChogGames.Games.ChogRoyale.Rules;

namespace ChogGames.Games.ChogRoyale.Tests.Rules;

/// <summary>
/// Rules version 2 (STORY-079, DEC-090): version 1 with Chaos and a successful Whale Swap paying their player, and longer
/// to decide and between rounds; new matches start with it, and a match started with version 1 plays on with version 1.
/// </summary>
public class RoyaleRulesV2Tests
{
    // Deal draws: 0 Pump, 65 Whale Swap, 90 Chaos; event 0 Calm Market.
    private const int Pump = 0;
    private const int Whale = 65;
    private const int Chaos = 90;

    private static readonly ChogRoyaleMatchRules Both = new([RoyaleRulesV1.Rules, RoyaleRulesV2.Rules]);

    private static RoyaleTable Dealt(params int[] cards) =>
        new(cards.Length, new Draws([.. cards.SelectMany(card => new[] { card, card, card }), 0]), Both);

    private static RoyaleLedgerEntry Entry(RoyaleTable table, int seat) => table.Stored.Ledger[^1].Entries.Single(e => e.Seat == seat);

    [Fact]
    public void Version_2_is_version_1_with_chaos_and_whale_swap_paying_their_player()
    {
        var (v1, v2) = (RoyaleRulesV1.Rules, RoyaleRulesV2.Rules);

        Assert.Equal(2, v2.Version);
        Assert.Equal(v1.Effects with { ChaosGain = 10, SwapBonus = 10 }, v2.Effects);
        Assert.Equal((0, 0), (v1.Effects.ChaosGain, v1.Effects.SwapBonus));
        Assert.Equal((v1.Rounds, v1.TimeCap, v1.StartingMon, v1.MaxMon, v1.HandSize), (v2.Rounds, v2.TimeCap, v2.StartingMon, v2.MaxMon, v2.HandSize));
        Assert.Equal(v1.Deck, v2.Deck);
        Assert.Equal(v1.Market.Events, v2.Market.Events);
    }

    [Fact]
    public void Version_2_gives_25_s_to_decide_and_20_s_between_rounds_within_the_cap()
    {
        var v2 = RoyaleRulesV2.Rules;

        Assert.Equal(new RoyaleTiming(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(25), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5)), v2.Timing);
        var rounds = v2.Timing.ToRounds();
        Assert.Equal((TimeSpan.FromSeconds(25), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(20)), (rounds.Decision, rounds.FirstOpensAfter, rounds.OpensAfter));
        Assert.True(v2.Timing.Round * v2.Rounds <= v2.TimeCap);
        Assert.Equal(TimeSpan.FromSeconds(15), RoyaleRulesV1.Rules.Timing.Decision);
    }

    [Fact]
    public void New_matches_start_with_version_2()
    {
        Assert.Equal(2, Dealt(Pump, Pump).Stored.RulesVersion);
    }

    [Fact]
    public void Every_chaos_gains_10_and_only_the_first_draws_the_modifier()
    {
        var table = Dealt(Chaos, Chaos);
        table.Play(1, 0);
        table.Play(2, 0);

        table.Resolve();

        Assert.Equal((RoyaleOutcome.Applied, 10, 110), (Entry(table, 1).Outcome!.Value, Entry(table, 1).Amount, Entry(table, 1).Balance));
        Assert.Equal((RoyaleOutcome.Taken, 10, 110), (Entry(table, 2).Outcome!.Value, Entry(table, 2).Amount, Entry(table, 2).Balance));
    }

    [Fact]
    public void A_successful_whale_swap_gains_10_on_top_of_the_exchange()
    {
        var table = Dealt(Whale, Pump);
        table.Play(1, 0, target: 2);

        table.Resolve(state => state with { Players = [.. state.Players.Select(p => p with { Mon = p.Seat == 1 ? 50 : 100 })] });

        // Seat 1 gives 10 and gets 20, plus the bonus: 50 + 10 + 10.
        Assert.Equal((70, 90), (table.Mon(1), table.Mon(2)));
        Assert.Equal((20, 10), (Entry(table, 1).Amount, Entry(table, 2).Lost));
    }

    [Fact]
    public void A_blocked_whale_swap_gains_nothing()
    {
        // Seat 2 holds Diamond Hands (50).
        var table = new RoyaleTable(2, new Draws(Whale, Whale, Whale, 50, 50, 50, 0), Both);
        table.Play(1, 0, target: 2);
        table.Play(2, 0);

        table.Resolve();

        Assert.Equal((RoyaleOutcome.Blocked, 0, 100), (Entry(table, 1).Outcome!.Value, Entry(table, 1).Amount, table.Mon(1)));
    }

    [Fact]
    public void A_match_started_with_version_1_plays_on_with_version_1()
    {
        var table = new RoyaleTable(2, new Draws(Chaos, Chaos, Chaos, Chaos, Chaos, Chaos, 0), new ChogRoyaleMatchRules([RoyaleRulesV1.Rules]));
        var later = new ChogRoyaleMatchRules([RoyaleRulesV1.Rules, RoyaleRulesV2.Rules]);
        table.Play(1, 0);

        // The deploy that adds version 2 resolves the round with the match's own version.
        var resolution = later.ResolveRound(new Core.Matches.MatchRoundContext(table.Match, table.State, table.Random));

        var entry = System.Text.Json.JsonSerializer.Deserialize<RoyaleMatchState>(resolution.State, RoyaleTable.Json)!.Ledger[^1].Entries.Single(e => e.Seat == 1);
        Assert.Equal((RoyaleOutcome.Applied, 0, 100), (entry.Outcome!.Value, entry.Amount, entry.Balance));
    }
}
