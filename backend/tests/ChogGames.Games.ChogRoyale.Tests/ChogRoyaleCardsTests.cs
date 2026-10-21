using System.Text.Json;
using ChogGames.Core.Matches;
using ChogGames.Core.Rooms;
using ChogGames.Games.ChogRoyale.Rules;

namespace ChogGames.Games.ChogRoyale.Tests;

/// <summary>
/// What the cards and the market do, and how a match ends: Pump, Rug Pull and Diamond Hands (STORY-075, DEC-086), Whale
/// Swap, Airdrop and Chaos (STORY-076, DEC-087), market events (STORY-077, DEC-088), and the final ranking with its
/// tie-breaks (STORY-078, DEC-089).
/// </summary>
public class ChogRoyaleCardsTests
{
    // Deal draws: 0 Pump, 30 Rug Pull, 50 Diamond Hands, 65 Whale Swap, 75 Airdrop, 90 Chaos.
    private const int Pump = 0;
    private const int Rug = 30;
    private const int Diamond = 50;
    private const int Whale = 65;
    private const int Airdrop = 75;
    private const int Chaos = 90;

    // Event draws: 0 Calm Market, 30 Meme Hype, 50 Market Crash, 70 Whale Incoming, 85 Community Airdrop.
    private const int Calm = 0;
    private const int Hype = 30;

    /// <summary>A table whose seats are each dealt three of the given card, in seat order, and whose first event is Calm Market.</summary>
    private static RoyaleTable Dealt(params int[] cards) =>
        new(cards.Length, new Draws([.. cards.SelectMany(card => new[] { card, card, card }), Calm]));

    /// <summary>The state with these balances, by seat.</summary>
    private static Func<RoyaleMatchState, RoyaleMatchState> Holding(params int[] mon) =>
        state => state with { Players = [.. state.Players.Select(p => p with { Mon = mon[p.Seat - 1] })] };

    private static Func<RoyaleMatchState, RoyaleMatchState> During(RoyaleMarketEvent marketEvent, params int[] mon) =>
        state => (mon.Length > 0 ? Holding(mon)(state) : state) with { Event = marketEvent };

    private static RoyaleLedgerEntry Entry(RoyaleTable table, int seat) =>
        table.Stored.Ledger[^1].Entries.Single(e => e.Seat == seat);

    private static int[] Balances(RoyaleTable table) => [.. table.Stored.Players.OrderBy(p => p.Seat).Select(p => p.Mon)];

    // STORY-075: Pump, Rug Pull and Diamond Hands.

    [Fact]
    public void A_pump_gains_20()
    {
        var table = Dealt(Pump, Pump);
        table.Play(1, 0);

        table.Resolve();

        Assert.Equal([120, 100], Balances(table));
        var entry = Entry(table, 1);
        Assert.Equal((RoyaleOutcome.Applied, 20, 20, 120), (entry.Outcome!.Value, entry.Amount, entry.Change, entry.Balance));
    }

    [Fact]
    public void A_rug_pull_steals_15_from_its_target()
    {
        var table = Dealt(Rug, Pump);
        table.Play(1, 0, target: 2);

        table.Resolve();

        Assert.Equal([115, 85], Balances(table));
        Assert.Equal((RoyaleOutcome.Applied, 15, 0), (Entry(table, 1).Outcome!.Value, Entry(table, 1).Amount, Entry(table, 1).Lost));
        Assert.Equal((15, -15, (RoyaleOutcome?)null), (Entry(table, 2).Lost, Entry(table, 2).Change, Entry(table, 2).Outcome));
    }

    [Fact]
    public void A_rug_pull_on_a_player_holding_8_takes_8()
    {
        var table = Dealt(Rug, Pump);
        table.Play(1, 0, target: 2);

        table.Resolve(Holding(100, 8));

        Assert.Equal([108, 0], Balances(table));
        Assert.Equal((8, 8), (Entry(table, 1).Amount, Entry(table, 2).Lost));
    }

    [Fact]
    public void A_rug_pull_on_an_empty_player_takes_nothing_and_does_not_succeed()
    {
        var table = Dealt(Rug, Pump);
        table.Play(1, 0, target: 2);

        table.Resolve(Holding(100, 0));

        Assert.Equal([100, 0], Balances(table));
        Assert.Equal((RoyaleOutcome.Empty, 0), (Entry(table, 1).Outcome!.Value, Entry(table, 1).Amount));
    }

    [Fact]
    public void Rug_pulls_on_one_victim_take_in_priority_order_and_never_more_than_they_hold()
    {
        var table = Dealt(Rug, Rug, Pump);
        table.Play(1, 0, target: 3);
        table.Play(2, 0, target: 3);

        table.Resolve(Holding(100, 100, 20));

        Assert.Equal([115, 105, 0], Balances(table));
        Assert.Equal((15, 5, 20), (Entry(table, 1).Amount, Entry(table, 2).Amount, Entry(table, 3).Lost));
    }

    [Fact]
    public void A_rug_pull_reads_the_victims_balance_after_the_gains()
    {
        var table = Dealt(Rug, Pump);
        table.Play(1, 0, target: 2);
        table.Play(2, 0);

        table.Resolve(Holding(100, 5));

        Assert.Equal([115, 10], Balances(table));
    }

    [Fact]
    public void A_shielded_player_hit_twice_loses_only_to_the_second_attack()
    {
        var table = Dealt(Rug, Rug, Diamond);
        table.Play(1, 0, target: 3);
        table.Play(2, 0, target: 3);
        table.Play(3, 0);

        table.Resolve();

        Assert.Equal([100, 115, 90], Balances(table));
        Assert.Equal((RoyaleOutcome.Blocked, 0), (Entry(table, 1).Outcome!.Value, Entry(table, 1).Amount));
        Assert.Equal((RoyaleOutcome.Applied, 15), (Entry(table, 2).Outcome!.Value, Entry(table, 2).Amount));
        var shielded = Entry(table, 3);
        Assert.Equal((RoyaleDefense.Blocked, RoyaleOutcome.Applied, 5, 15, -10), (shielded.Defense!.Value, shielded.Outcome!.Value, shielded.Amount, shielded.Lost, shielded.Change));
    }

    [Fact]
    public void The_shield_blocks_the_first_attack_in_the_rounds_rotated_priority()
    {
        var table = Dealt(Rug, Rug, Diamond);
        table.Resolve();
        table.Play(1, 0, target: 3);
        table.Play(2, 0, target: 3);
        table.Play(3, 0);

        // Round 2 resolves from seat 2: its Rug Pull is the one blocked.
        table.Resolve();

        Assert.Equal([2, 3, 1], table.Stored.Ledger[^1].Priority);
        Assert.Equal((RoyaleOutcome.Applied, RoyaleOutcome.Blocked), (Entry(table, 1).Outcome!.Value, Entry(table, 2).Outcome!.Value));
    }

    [Fact]
    public void Diamond_hands_with_nothing_aimed_at_its_player_gains_5_and_stays_raised()
    {
        var table = Dealt(Diamond, Rug, Pump);
        table.Play(1, 0);
        table.Play(2, 0, target: 3);

        table.Resolve();

        Assert.Equal([105, 115, 85], Balances(table));
        Assert.Equal((RoyaleDefense.Raised, 5), (Entry(table, 1).Defense!.Value, Entry(table, 1).Amount));
        Assert.Null(Entry(table, 3).Defense);
    }

    // STORY-076: Whale Swap, Airdrop and Chaos.

    [Fact]
    public void A_whale_swap_exchanges_a_fifth_of_each_pre_round_balance_within_20_each_way()
    {
        var table = Dealt(Whale, Pump);
        table.Play(1, 0, target: 2);

        table.Resolve(Holding(50, 200));

        // Seat 1 gives 10 (20% of 50) and gets 20 (20% of 200, capped).
        Assert.Equal([60, 190], Balances(table));
        Assert.Equal((RoyaleOutcome.Applied, 10), (Entry(table, 1).Outcome!.Value, Entry(table, 1).Amount));
        Assert.Equal(10, Entry(table, 2).Lost);
    }

    [Fact]
    public void A_whale_swap_that_loses_its_player_mon_is_not_a_loss_taken()
    {
        var table = Dealt(Whale, Pump);
        table.Play(1, 0, target: 2);

        table.Resolve(Holding(200, 50));

        Assert.Equal([190, 60], Balances(table));
        Assert.Equal((-10, 0, 0), (Entry(table, 1).Amount, Entry(table, 1).Lost, Entry(table, 2).Lost));
    }

    [Fact]
    public void A_whale_swap_reads_the_snapshot_not_the_rounds_gains()
    {
        var table = Dealt(Whale, Pump);
        table.Play(1, 0, target: 2);
        table.Play(2, 0);

        table.Resolve(Holding(40, 60));

        // Seat 2 pumps to 80 but gives 12, a fifth of its 60 before the round; seat 1 gives 8.
        Assert.Equal([44, 76], Balances(table));
    }

    [Fact]
    public void A_whale_swap_gives_no_more_than_its_side_holds_by_then()
    {
        var table = Dealt(Whale, Pump, Rug);
        table.Play(1, 0, target: 2);
        table.Play(3, 0, target: 2);

        // Whale Incoming lets the Rug Pull take all 20 of seat 2's MON before the swap, so seat 2 gives nothing.
        table.Resolve(During(RoyaleMarketEvent.WhaleIncoming, 100, 20, 100));

        Assert.Equal([80, 20, 120], Balances(table));
        Assert.Equal((RoyaleOutcome.Applied, -20), (Entry(table, 1).Outcome!.Value, Entry(table, 1).Amount));
        Assert.All(table.Stored.Players, p => Assert.True(p.Mon >= 0));
    }

    [Fact]
    public void Overlapping_swaps_leave_each_player_in_at_most_one_the_earliest_in_priority()
    {
        var table = Dealt(Whale, Whale, Whale, Whale);
        table.Play(1, 0, target: 2);
        table.Play(2, 0, target: 3);
        table.Play(3, 0, target: 4);
        table.Play(4, 0, target: 1);

        table.Resolve(Holding(50, 100, 50, 100));

        Assert.Equal(
            [RoyaleOutcome.Applied, RoyaleOutcome.Taken, RoyaleOutcome.Applied, RoyaleOutcome.Taken],
            table.Stored.Ledger[^1].Entries.Select(e => e.Outcome!.Value));
        Assert.Equal([60, 90, 60, 90], Balances(table));
    }

    [Fact]
    public void Two_players_swapping_with_each_other_swap_once()
    {
        var table = Dealt(Whale, Whale);
        table.Play(1, 0, target: 2);
        table.Play(2, 0, target: 1);

        table.Resolve(Holding(50, 100));

        Assert.Equal([60, 90], Balances(table));
        Assert.Equal((RoyaleOutcome.Applied, RoyaleOutcome.Taken), (Entry(table, 1).Outcome!.Value, Entry(table, 2).Outcome!.Value));
    }

    [Fact]
    public void A_swap_with_a_shielded_target_is_blocked_but_a_swap_that_cannot_happen_does_not_spend_the_shield()
    {
        var table = Dealt(Whale, Whale, Diamond, Whale);
        table.Play(1, 0, target: 2);
        table.Play(2, 0, target: 3);
        table.Play(3, 0);
        table.Play(4, 0, target: 3);

        table.Resolve(Holding(50, 100, 100, 50));

        Assert.Equal(
            [RoyaleOutcome.Applied, RoyaleOutcome.Taken, RoyaleOutcome.Applied, RoyaleOutcome.Blocked],
            table.Stored.Ledger[^1].Entries.Select(e => e.Outcome!.Value));
        Assert.Equal(RoyaleDefense.Blocked, Entry(table, 3).Defense);
        Assert.Equal([60, 90, 105, 50], Balances(table));
    }

    [Theory]
    [InlineData(5)]
    [InlineData(17)]
    [InlineData(25)]
    public void An_airdrop_gains_5_to_25_from_the_servers_randomness_and_logs_it(int drawn)
    {
        var draws = new Draws(Airdrop, Airdrop, Airdrop, Pump, Pump, Pump, Calm);
        var table = new RoyaleTable(2, draws);
        table.Play(1, 0);
        draws.Enqueue(drawn);
        var before = draws.Asked.Count;

        table.Resolve();

        Assert.Equal((5, 26), draws.Asked[before]);
        Assert.Equal(100 + drawn, table.Mon(1));
        Assert.Equal((RoyaleOutcome.Applied, drawn), (Entry(table, 1).Outcome!.Value, Entry(table, 1).Amount));
    }

    [Fact]
    public void Several_chaos_cards_add_one_modifier_drawn_by_the_first_in_priority()
    {
        var draws = new Draws(Chaos, Chaos, Chaos, Chaos, Chaos, Chaos, Chaos, Chaos, Chaos, Calm);
        var table = new RoyaleTable(3, draws);
        table.Play(1, 0);
        table.Play(2, 0);
        table.Play(3, 0);
        var before = draws.Asked.Count;

        // Under Calm Market the pool is the four modifiers: a draw of 3 is Community Airdrop.
        draws.Enqueue(3);
        table.Resolve();

        Assert.Equal([(0, 4), (0, 100), (0, 100), (0, 100), (0, 100)], draws.Asked[before..]);
        var round = table.Stored.Ledger[^1];
        Assert.Equal((RoyaleMarketEvent.CalmMarket, RoyaleMarketEvent.CommunityAirdrop), (round.Event, round.Chaos!.Value));
        Assert.Equal([RoyaleOutcome.Applied, RoyaleOutcome.Taken, RoyaleOutcome.Taken], round.Entries.Select(e => e.Outcome!.Value));
        Assert.Equal([105, 105, 105], Balances(table));
    }

    [Fact]
    public void Chaos_never_draws_the_rounds_own_event_and_its_modifier_stacks_with_it()
    {
        var draws = new Draws(Chaos, Chaos, Chaos, Pump, Pump, Pump, Hype);
        var table = new RoyaleTable(2, draws);
        table.Play(1, 0);
        table.Play(2, 0);

        // Under Meme Hype the pool is Market Crash, Whale Incoming, Community Airdrop: a draw of 0 is Market Crash.
        draws.Enqueue(0);
        table.Resolve();

        Assert.Equal(RoyaleMarketEvent.MarketCrash, table.Stored.Ledger[^1].Chaos);
        Assert.Equal(115, table.Mon(2));
    }

    [Fact]
    public void No_chaos_played_draws_no_modifier()
    {
        var table = Dealt(Pump, Pump);
        table.Play(1, 0);

        table.Resolve();

        Assert.Null(table.Stored.Ledger[^1].Chaos);
    }

    // STORY-077: market events.

    [Fact]
    public void Every_player_sees_the_same_event_before_deciding_and_each_round_draws_the_next()
    {
        var draws = new Draws(Pump, Pump, Pump, Pump, Pump, Pump, Hype);
        var table = new RoyaleTable(2, draws);

        Assert.Equal(["MemeHype", "MemeHype"], new[] { 1, 2 }.Select(seat => table.ViewOf(seat).GetProperty("event").GetString()));
        Assert.True(table.Match.RoundOpensAt > RoyaleTable.Start);

        // The round's replacement card first, then round 2's event: 50 is Market Crash.
        table.Play(1, 0, locks: true);
        draws.Enqueue(Pump, 50);
        table.Resolve();

        Assert.Equal(RoyaleMarketEvent.MemeHype, table.Stored.Ledger[^1].Event);
        Assert.Equal(["MarketCrash", "MarketCrash"], new[] { 1, 2 }.Select(seat => table.ViewOf(seat).GetProperty("event").GetString()));
    }

    [Fact]
    public void Decisions_never_draw_or_change_the_event()
    {
        var draws = new Draws(Rug, Rug, Rug, Pump, Pump, Pump, Hype);
        var table = new RoyaleTable(2, draws);
        var dealt = draws.Asked.Count;

        table.Play(1, 0, target: 2);
        table.Hodl(2);
        table.Play(1, 1, target: 2, locks: true);

        Assert.Equal(dealt, draws.Asked.Count);
        Assert.Equal(RoyaleMarketEvent.MemeHype, table.Stored.Event);
    }

    [Theory]
    [InlineData(RoyaleMarketEvent.CalmMarket, 20)]
    [InlineData(RoyaleMarketEvent.MemeHype, 30)]
    [InlineData(RoyaleMarketEvent.MarketCrash, 10)]
    [InlineData(RoyaleMarketEvent.WhaleIncoming, 20)]
    [InlineData(RoyaleMarketEvent.CommunityAirdrop, 20)]
    public void A_pump_pays_30_under_meme_hype_and_10_under_market_crash(RoyaleMarketEvent marketEvent, int gain)
    {
        var table = Dealt(Pump, Pump);
        table.Play(1, 0);

        table.Resolve(During(marketEvent));

        Assert.Equal(gain, Entry(table, 1).Amount);
    }

    [Fact]
    public void Whale_incoming_adds_5_to_a_successful_rug_within_the_victims_balance()
    {
        var table = Dealt(Rug, Rug, Pump);
        table.Play(1, 0, target: 3);
        table.Play(2, 0, target: 3);

        table.Resolve(During(RoyaleMarketEvent.WhaleIncoming, 100, 100, 28));

        Assert.Equal([120, 108, 0], Balances(table));
    }

    [Fact]
    public void Whale_incoming_adds_nothing_to_a_blocked_rug()
    {
        var table = Dealt(Rug, Diamond);
        table.Play(1, 0, target: 2);
        table.Play(2, 0);

        table.Resolve(During(RoyaleMarketEvent.WhaleIncoming));

        Assert.Equal([100, 105], Balances(table));
    }

    [Fact]
    public void Community_airdrop_gives_everyone_5_after_the_cards()
    {
        var table = Dealt(Rug, Pump, Pump);
        table.Play(1, 0, target: 2);

        table.Resolve(During(RoyaleMarketEvent.CommunityAirdrop, 100, 10, 100));

        // Seat 2 is robbed of all 10 first, then gets the 5.
        Assert.Equal([115, 5, 105], Balances(table));
        Assert.All(table.Stored.Ledger[^1].Entries, e => Assert.Equal(5, e.Market));
        Assert.Equal((10, -5), (Entry(table, 2).Lost, Entry(table, 2).Change));
    }

    [Fact]
    public void Balances_are_clamped_to_the_maximum_after_the_market()
    {
        var table = Dealt(Pump, Pump);
        table.Play(1, 0);

        table.Resolve(During(RoyaleMarketEvent.CommunityAirdrop, 9_990, 100));

        Assert.Equal((10_000, 10), (table.Mon(1), Entry(table, 1).Change));
    }

    // STORY-078: finish, rankings and results.

    [Fact]
    public void Standings_rank_by_mon_then_fewer_losses_then_more_successful_cards_and_exact_ties_share_a_place()
    {
        var table = new RoyaleTable(6);
        RoyaleLedgerEntry Played(int seat, RoyaleOutcome outcome, int lost) =>
            new(seat, RoyaleCard.Pump, null, false, outcome, 0, null, lost, 0, 0, 0);
        RoyaleRoundLedger Round(int round, params RoyaleLedgerEntry[] entries) =>
            new(round, RoyaleMarketEvent.CalmMarket, null, [1, 2, 3, 4, 5, 6], entries);
        var ledger = new[]
        {
            Round(1, Played(1, RoyaleOutcome.Applied, 0), Played(2, RoyaleOutcome.Applied, 10), Played(3, RoyaleOutcome.Applied, 5), Played(4, RoyaleOutcome.Applied, 5), Played(5, RoyaleOutcome.Blocked, 5), Played(6, RoyaleOutcome.Applied, 0)),
            Round(2, Played(1, RoyaleOutcome.Applied, 0), Played(2, RoyaleOutcome.Applied, 0), Played(3, RoyaleOutcome.Applied, 0), Played(4, RoyaleOutcome.Blocked, 0), Played(5, RoyaleOutcome.Applied, 0), Played(6, RoyaleOutcome.Empty, 0)),
        };
        int[] mon = [120, 120, 100, 100, 100, 90];
        var state = table.Stored with { Players = [.. table.Stored.Players.Select(p => p with { Mon = mon[p.Seat - 1] })], Ledger = ledger };

        var ending = table.Rules.EndMatch(new MatchEndContext(table.Match, JsonSerializer.SerializeToElement(state, RoyaleTable.Json), TimeUp: true))!;

        Assert.Equal([1, 2, 3, 4, 4, 6], Enumerable.Range(1, 6).Select(seat => ending.Standings.Single(s => s.PlayerId == table.Seat(seat)).Rank));
        Assert.Equal(
            """{"mon":100,"change":0,"lost":5,"successfulCards":2}""",
            ending.Standings.Single(s => s.PlayerId == table.Seat(3)).Result!.Value.GetRawText());
        Assert.Equal(
            """{"mon":120,"change":20,"lost":10,"successfulCards":2}""",
            ending.Standings.Single(s => s.PlayerId == table.Seat(2)).Result!.Value.GetRawText());
    }

    [Fact]
    public void After_round_five_the_match_ranks_its_players_with_their_results()
    {
        var table = Dealt(Pump, Rug, Pump);
        for (var round = 1; round <= 5; round++)
        {
            // Seat 1's played card is replaced with a Pump and seat 2's with a Rug Pull; the next event is Calm Market.
            ((Draws)table.Random).Enqueue(Pump, Rug, Calm);
            table.Play(1, 0, locks: true);
            table.Play(2, 0, target: 3, locks: true);
            table.Hodl(3, locks: true);
            table.Resolve();
        }

        Assert.Equal(RoomStatus.Completed, table.Match.Status);
        Assert.Equal([200, 175, 25], Balances(table));
        Assert.Equal([1, 2, 3], Enumerable.Range(1, 3).Select(seat => table.Match.Members.Single(m => m.Seat == seat).Rank!.Value));
        Assert.Equal(
            ["""{"mon":200,"change":100,"lost":0,"successfulCards":5}""", """{"mon":175,"change":75,"lost":0,"successfulCards":5}""", """{"mon":25,"change":-75,"lost":75,"successfulCards":0}"""],
            Enumerable.Range(1, 3).Select(seat => table.Match.Members.Single(m => m.Seat == seat).Result));
    }

    [Fact]
    public void Commands_after_the_end_are_refused()
    {
        var table = new RoyaleTable(2);
        for (var round = 1; round <= 5; round++)
        {
            table.Resolve();
        }

        var command = new MatchCommand(Guid.NewGuid(), table.Match.Id, table.Match.Version, ChogRoyaleMatchRules.SubmitAction, JsonSerializer.SerializeToElement(new { hodl = true }));

        Assert.Equal(MatchCommandProblem.Closed, table.Match.CheckCommand(table.Seat(1), command, table.Now));
        Assert.Equal(RoomStatus.Completed, table.Match.Status);
    }
}
