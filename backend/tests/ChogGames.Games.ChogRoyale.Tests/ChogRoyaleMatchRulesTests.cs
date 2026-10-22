using System.Text.Json;
using System.Text.Json.Nodes;
using ChogGames.Core.Matches;
using ChogGames.Core.Rooms;
using ChogGames.Games.ChogRoyale.Rules;

namespace ChogGames.Games.ChogRoyale.Tests;

/// <summary>
/// Chog Royale's match state and rounds (STORY-071, DEC-082), hidden actions and HODL (STORY-072, DEC-083), deck and
/// hands (STORY-073, DEC-084), and round resolution with its ledger (STORY-074, DEC-085), played through the shared
/// engine's round mode. What the cards and the market do is in <see cref="ChogRoyaleCardsTests"/>.
/// </summary>
public class ChogRoyaleMatchRulesTests
{
    // Draws for the deal: 0 Pump, 30 Rug Pull, 50 Diamond Hands, 65 Whale Swap, 75 Airdrop, 90 Chaos.
    private const int Pump = 0;
    private const int Rug = 30;
    private const int Diamond = 50;
    private const int Whale = 65;
    private const int Airdrop = 75;
    private const int Chaos = 90;

    // An event draw of 0 is Calm Market.
    private const int Calm = 0;

    private static RoyaleCard[] Cards(RoyaleHandCard[] hand) => [.. hand.Select(c => c.Card)];

    private static string[] Ids(RoyaleHandCard[] hand) => [.. hand.Select(c => c.Id)];

    private static void Accepted(MatchRuleDecision decision) => Assert.Equal(MatchRuleOutcome.Accepted, decision.Outcome);

    private static void Rejected(string reason, MatchRuleDecision decision) =>
        Assert.Equal((MatchRuleOutcome.Rejected, reason), (decision.Outcome, decision.Reason));

    [Theory]
    [InlineData(2)]
    [InlineData(6)]
    public void A_match_starts_in_round_one_with_100_mon_and_three_cards_each(int players)
    {
        var draws = new Draws();
        var table = new RoyaleTable(players, draws);

        var state = table.Stored;
        Assert.Equal((1, 1, 3 * players + 1), (state.RulesVersion, state.Round, state.NextCard));
        Assert.Equal(Enumerable.Range(1, players), state.Players.Select(p => p.Seat));
        Assert.All(state.Players, p => Assert.Equal((100, 3, (RoyaleAction?)null), (p.Mon, p.Hand.Length, p.Action)));
        Assert.Equal(Enumerable.Range(1, 3 * players).Select(i => $"c{i}"), state.Players.SelectMany(p => Ids(p.Hand)));
        Assert.Equal(Enumerable.Repeat((0, 100), 3 * players + 1), draws.Asked);
        Assert.Equal(RoyaleMarketEvent.CalmMarket, state.Event);
        Assert.Empty(state.Ledger);
        Assert.Equal((MatchPlay.Rounds, 1, TimeSpan.FromSeconds(15)), (table.Match.Play, table.Match.TurnNumber, table.Match.TurnTimeout));
        Assert.Equal(RoyaleTable.Start + TimeSpan.FromSeconds(5), table.Match.RoundOpensAt);
        Assert.Equal(RoyaleTable.Start + TimeSpan.FromMinutes(5), table.Match.MatchEndsAt);
    }

    [Fact]
    public void Hands_are_dealt_in_seat_order_from_weighted_draws()
    {
        var table = new RoyaleTable(2, new Draws(Pump, Rug, Diamond, Whale, Airdrop, Chaos));

        Assert.Equal([RoyaleCard.Pump, RoyaleCard.RugPull, RoyaleCard.DiamondHands], Cards(table.Hand(1)));
        Assert.Equal([RoyaleCard.WhaleSwap, RoyaleCard.Airdrop, RoyaleCard.Chaos], Cards(table.Hand(2)));
    }

    [Fact]
    public void The_same_draws_deal_the_same_hands_and_give_the_same_rounds()
    {
        int[] seed = [12, 99, 41, 3, 77, 65, 30, 31, 88];
        var first = new RoyaleTable(3, new Draws(seed));
        var second = new RoyaleTable(3, new Draws(seed));

        foreach (var table in new[] { first, second })
        {
            Accepted(table.Play(1, 2, target: 2));
            Accepted(table.Hodl(2));
            ((Draws)table.Random).Enqueue(55, 5);
            table.Resolve();
        }

        Assert.Equal(first.Match.State, second.Match.State);
        Assert.NotEqual(first.Match.State, new RoyaleTable(3, new Draws(seed.Reverse().ToArray())).Match.State);
    }

    [Fact]
    public void A_match_plays_its_five_rounds_to_the_end_without_any_action()
    {
        var table = new RoyaleTable(3);

        for (var round = 1; round <= 5; round++)
        {
            Assert.Equal((RoomStatus.Active, round, round), (table.Match.Status, table.Match.TurnNumber, table.Stored.Round));
            var resolution = table.Resolve();
            Assert.Equal(round == 5, resolution.Standings is not null);
        }

        Assert.Equal(RoomStatus.Completed, table.Match.Status);
        Assert.All(table.Match.Members, m => Assert.Equal(1, m.Rank));
        Assert.All(table.Match.Members, m => Assert.Equal("""{"mon":100,"change":0,"lost":0,"successfulCards":0,"degenScore":0,"degenScoreVersion":1}""", m.Result));
        var state = table.Stored;
        Assert.Equal((5, 5), (state.Round, state.Ledger.Length));
        Assert.All(state.Ledger.SelectMany(r => r.Entries), e => Assert.Equal((true, (RoyaleCard?)null, 0, 100), (e.ByDefault, e.Card, e.Change, e.Balance)));
        Assert.All(state.Players, p => Assert.Equal((100, 3), (p.Mon, p.Hand.Length)));
    }

    [Fact]
    public void Each_player_sees_only_their_own_hand_and_pending_action()
    {
        var table = new RoyaleTable(3, new Draws(Pump, Rug, Diamond, Whale, Airdrop, Chaos, Pump, Pump, Pump));
        table.Play(1, 1, target: 3);

        var mine = table.ViewOf(1);
        var theirs = table.ViewOf(2);

        Assert.Equal(["c1", "c2", "c3"], mine.GetProperty("hand").EnumerateArray().Select(c => c.GetProperty("id").GetString()));
        Assert.Equal("""{"hodl":false,"card":"c2","target":3}""", mine.GetProperty("action").GetRawText());
        Assert.Equal(["c4", "c5", "c6"], theirs.GetProperty("hand").EnumerateArray().Select(c => c.GetProperty("id").GetString()));
        Assert.False(theirs.TryGetProperty("action", out _));
        foreach (var view in new[] { mine, theirs })
        {
            Assert.Equal((1, 5), (view.GetProperty("round").GetInt32(), view.GetProperty("rounds").GetInt32()));
            Assert.Equal(
                [(1, 100, 3), (2, 100, 3), (3, 100, 3)],
                view.GetProperty("players").EnumerateArray().Select(p => (p.GetProperty("seat").GetInt32(), p.GetProperty("mon").GetInt32(), p.GetProperty("cards").GetInt32())));
            Assert.All(view.GetProperty("players").EnumerateArray(), p => Assert.Equal(["seat", "mon", "cards"], p.EnumerateObject().Select(x => x.Name)));
        }

        Assert.DoesNotContain("\"c2\"", theirs.GetRawText());
        Assert.DoesNotContain("\"c4\"", mine.GetRawText());
    }

    [Fact]
    public void A_player_who_left_sees_no_hand_and_drops_out_of_the_players()
    {
        var table = new RoyaleTable(3);
        table.Match.Leave(table.Seat(3), table.Now);

        var left = table.ViewOf(3);

        Assert.Empty(left.GetProperty("hand").EnumerateArray());
        Assert.Equal([1, 2], table.ViewOf(1).GetProperty("players").EnumerateArray().Select(p => p.GetProperty("seat").GetInt32()));
    }

    [Fact]
    public void Only_the_last_valid_submission_counts()
    {
        var table = new RoyaleTable(2, new Draws(Pump, Airdrop, Diamond));
        Accepted(table.Play(1, 0));
        Accepted(table.Play(1, 1));
        Accepted(table.Hodl(1));
        Accepted(table.Play(1, 2, locks: true));

        Assert.Equal(new RoyaleAction(Card: "c3"), table.Stored.Players[0].Action);
        Assert.True(table.Match.Members.Single(m => m.Seat == 1).RoundLocked);
        table.Resolve();
        Assert.Equal(RoyaleCard.DiamondHands, table.Stored.Ledger[0].Entries[0].Card);
    }

    [Fact]
    public void A_submission_without_a_lock_leaves_the_round_open_and_a_lock_of_the_same_action_closes_it()
    {
        var table = new RoyaleTable(2);
        var open = table.Hodl(1);
        Accepted(open);
        Assert.False(open.Locks);

        Rejected("already_submitted", table.Hodl(1));
        var locked = table.Hodl(1, locks: true);

        Accepted(locked);
        Assert.True(locked.Locks);
    }

    public static TheoryData<string, object> InvalidActions => new()
    {
        { "nothing", new { } },
        { "lock only", new { @lock = true } },
        { "HODL false and no card", new { hodl = false } },
        { "HODL and a card", new { hodl = true, card = "c1" } },
        { "HODL with a target", new { hodl = true, target = 2 } },
        { "card as a number", new { card = 1 } },
        { "empty card", new { card = "" } },
        { "target as text", new { card = "c1", target = "2" } },
        { "fractional target", new { card = "c2", target = 2.5 } },
        { "lock as text", new { hodl = true, @lock = "yes" } },
        { "unknown property", new { hodl = true, amount = 50 } },
    };

    [Theory]
    [MemberData(nameof(InvalidActions))]
    public void Malformed_actions_are_refused_without_a_change_or_a_draw(string why, object payload)
    {
        var draws = new Draws();
        var table = new RoyaleTable(2, draws);
        var before = (table.Match.State, table.Match.Version, draws.Asked.Count);

        Rejected("invalid_action", table.Submit(1, payload));

        Assert.NotNull(why);
        Assert.Equal(before, (table.Match.State, table.Match.Version, draws.Asked.Count));
    }

    [Fact]
    public void A_card_not_in_the_hand_and_bad_targets_are_refused_without_a_change()
    {
        // Seat 1: Pump, Rug Pull, Whale Swap; seat 2 and 3 hold Pumps.
        var table = new RoyaleTable(3, new Draws(Pump, Rug, Whale));
        table.Match.Leave(table.Seat(3), table.Now);
        var before = (table.Match.State, table.Match.Version);

        Rejected("card_not_in_hand", table.Submit(1, new { card = "c4" }));
        Rejected("card_not_in_hand", table.Submit(1, new { card = "c99" }));
        Rejected("target_required", table.Play(1, 1));
        Rejected("target_required", table.Play(1, 2));
        Rejected("invalid_target", table.Play(1, 1, target: 1));
        Rejected("invalid_target", table.Play(1, 2, target: 3));
        Rejected("invalid_target", table.Play(1, 1, target: 7));
        Rejected("invalid_target", table.Play(1, 0, target: 2));

        Assert.Equal(before, (table.Match.State, table.Match.Version));
        Accepted(table.Play(1, 1, target: 2));
    }

    [Fact]
    public void Commands_other_than_submit_action_are_unknown()
    {
        var table = new RoyaleTable(2);

        var decision = table.Rules.Apply(new MatchCommandContext(
            table.Match, table.Seat(1), "ROLL", JsonSerializer.SerializeToElement(new { }), table.State, table.Random));

        Assert.Equal(MatchRuleOutcome.UnknownCommand, decision.Outcome);
    }

    [Fact]
    public void An_idle_player_hodls_at_the_deadline_and_keeps_their_hand()
    {
        var table = new RoyaleTable(2, new Draws(Pump, Pump, Pump, Rug, Rug, Rug));
        table.Play(1, 0, locks: true);
        var idleHand = table.Hand(2);

        table.Resolve();

        var entries = table.Stored.Ledger.Single().Entries;
        Assert.Equal((1, RoyaleCard.Pump, false), (entries[0].Seat, entries[0].Card!.Value, entries[0].ByDefault));
        Assert.Equal((2, (RoyaleCard?)null, true), (entries[1].Seat, entries[1].Card, entries[1].ByDefault));
        Assert.Equal(idleHand, table.Hand(2));
        Assert.Equal(1, table.Match.Members.Single(m => m.Seat == 2).MissedTurns);
    }

    [Fact]
    public void A_chosen_hodl_plays_nothing_and_keeps_the_hand()
    {
        var draws = new Draws();
        var table = new RoyaleTable(2, draws);
        var hand = table.Hand(1);
        table.Hodl(1, locks: true);
        table.Hodl(2, locks: true);
        var dealt = draws.Asked.Count;

        table.Resolve();

        var entry = table.Stored.Ledger.Single().Entries[0];
        Assert.Equal(((RoyaleCard?)null, false, 0), (entry.Card, entry.ByDefault, entry.Change));
        Assert.Equal(hand, table.Hand(1));

        // No card is dealt; the only draw is round 2's market event.
        Assert.Equal([(0, 100)], draws.Asked[dealt..]);
    }

    [Fact]
    public void A_played_card_is_replaced_with_a_new_id_after_the_round_in_seat_order()
    {
        var draws = new Draws(Pump, Rug, Diamond, Pump, Pump, Pump);
        var table = new RoyaleTable(2, draws);
        table.Play(2, 0, locks: true);
        table.Play(1, 1, target: 2, locks: true);
        draws.Enqueue(Chaos, Airdrop);

        table.Resolve();

        Assert.Equal(["c1", "c3", "c7"], Ids(table.Hand(1)));
        Assert.Equal([RoyaleCard.Pump, RoyaleCard.DiamondHands, RoyaleCard.Chaos], Cards(table.Hand(1)));
        Assert.Equal(["c5", "c6", "c8"], Ids(table.Hand(2)));
        Assert.Equal(RoyaleCard.Airdrop, table.Hand(2)[2].Card);
        Assert.Equal(9, table.Stored.NextCard);
    }

    [Fact]
    public void Card_ids_stay_unique_for_the_whole_match_and_no_card_is_dealt_after_the_last_round()
    {
        var table = new RoyaleTable(2);
        for (var round = 1; round <= 5; round++)
        {
            table.Play(1, 0, locks: true);
            table.Play(2, 2, locks: true);
            table.Resolve();
        }

        var state = table.Stored;
        Assert.Equal(RoomStatus.Completed, table.Match.Status);
        Assert.All(state.Players, p => Assert.Equal(2, p.Hand.Length));
        Assert.Equal(6 + 2 * 4 + 1, state.NextCard);
        var played = state.Ledger.SelectMany(r => r.Entries).Count(e => e.Card is not null);
        Assert.Equal(10, played);
        Assert.Equal(state.Players.SelectMany(p => Ids(p.Hand)).Distinct().Count(), state.Players.Sum(p => p.Hand.Length));
    }

    [Theory]
    [InlineData(new[] { 1, 2, 3 }, 1, new[] { 1, 2, 3 })]
    [InlineData(new[] { 1, 2, 3 }, 2, new[] { 2, 3, 1 })]
    [InlineData(new[] { 1, 2, 3 }, 3, new[] { 3, 1, 2 })]
    [InlineData(new[] { 1, 2, 3 }, 4, new[] { 1, 2, 3 })]
    [InlineData(new[] { 4, 1, 3 }, 2, new[] { 3, 4, 1 })]
    [InlineData(new[] { 2 }, 5, new[] { 2 })]
    [InlineData(new int[0], 1, new int[0])]
    public void Seat_priority_rotates_by_one_each_round(int[] seats, int round, int[] priority)
    {
        Assert.Equal(priority, ChogRoyaleMatchRules.Priority(seats, round));
    }

    [Fact]
    public void Each_round_records_its_rotated_priority_and_a_ledger_entry_per_player_in_seat_order()
    {
        var table = new RoyaleTable(3, new Draws(Rug, Rug, Rug));
        table.Play(1, 0, target: 3);

        table.Resolve();
        table.Resolve();
        table.Match.Leave(table.Seat(2), table.Now);
        table.Resolve();

        var ledger = table.Stored.Ledger;
        Assert.Equal([1, 2, 3], ledger.Select(r => r.Round));
        Assert.Equal([[1, 2, 3], [2, 3, 1], [1, 3]], ledger.Select(r => r.Priority));
        Assert.Equal(new RoyaleLedgerEntry(1, RoyaleCard.RugPull, 3, false, RoyaleOutcome.Applied, 15, null, 0, 0, 15, 115), ledger[0].Entries[0]);
        Assert.Equal((RoyaleMarketEvent.CalmMarket, (RoyaleMarketEvent?)null), (ledger[0].Event, ledger[0].Chaos));
        Assert.Equal([1, 2, 3], ledger[0].Entries.Select(e => e.Seat));
        Assert.Equal([1, 3], ledger[2].Entries.Select(e => e.Seat));
    }

    [Fact]
    public void A_card_aimed_at_a_player_who_left_is_still_played_and_replaced()
    {
        var table = new RoyaleTable(3, new Draws(Rug, Rug, Rug));
        table.Play(1, 0, target: 3, locks: true);
        table.Match.Leave(table.Seat(3), table.Now);

        table.Resolve();

        var entry = table.Stored.Ledger.Single().Entries[0];
        Assert.Equal((RoyaleCard.RugPull, 3, RoyaleOutcome.TargetLeft, 100), (entry.Card!.Value, entry.Target!.Value, entry.Outcome, entry.Balance));
        Assert.Equal(["c2", "c3", "c10"], Ids(table.Hand(1)));
    }

    [Theory]
    [InlineData(RoyaleCard.Chaos, ChogRoyaleMatchRules.Phase.Chaos)]
    [InlineData(RoyaleCard.DiamondHands, ChogRoyaleMatchRules.Phase.Defenses)]
    [InlineData(RoyaleCard.Pump, ChogRoyaleMatchRules.Phase.Gains)]
    [InlineData(RoyaleCard.Airdrop, ChogRoyaleMatchRules.Phase.Gains)]
    [InlineData(RoyaleCard.RugPull, ChogRoyaleMatchRules.Phase.Rugs)]
    [InlineData(RoyaleCard.WhaleSwap, ChogRoyaleMatchRules.Phase.Swaps)]
    public void Each_card_acts_in_its_phase(RoyaleCard card, ChogRoyaleMatchRules.Phase phase)
    {
        Assert.Equal(phase, ChogRoyaleMatchRules.PhaseOf(card));
    }

    [Fact]
    public void The_phases_run_in_the_documented_order()
    {
        Assert.Equal(
            ["Snapshot", "Chaos", "Defenses", "Gains", "Rugs", "Swaps", "Market", "Clamp"],
            Enum.GetNames<ChogRoyaleMatchRules.Phase>());
    }

    [Fact]
    public void Resolving_the_same_state_again_gives_the_same_result_and_the_engine_resolves_a_round_once()
    {
        var table = new RoyaleTable(2, new Draws(Pump, Pump, Pump, Pump, Pump, Pump, Calm, Rug, Rug));
        table.Play(1, 0, locks: true);
        table.Play(2, 1, locks: true);
        var context = new MatchRoundContext(table.Match, table.State, new Draws(Rug, Rug));

        var once = table.Rules.ResolveRound(context).State.GetRawText();
        var again = table.Rules.ResolveRound(context with { Random = new Draws(Rug, Rug) }).State.GetRawText();
        table.Resolve();
        var stored = table.Match.State;

        Assert.Equal(once, again);
        Assert.Equal(JsonNode.Parse(once)!.ToJsonString(), JsonNode.Parse(stored!)!.ToJsonString());
        Assert.Throws<InvalidOperationException>(() => table.Match.ResolveRound(once, table.Now, TimeSpan.Zero));
        Assert.Equal(stored, table.Match.State);
    }

    [Fact]
    public void The_time_cap_ranks_by_mon_sharing_places_and_drops_pending_actions()
    {
        var table = new RoyaleTable(3);
        table.Hodl(1);
        var state = JsonNode.Parse(table.Match.State!)!;
        state["players"]![0]!["mon"] = 140;
        state["players"]![1]!["mon"] = 90;
        state["players"]![2]!["mon"] = 140;

        var ending = table.Rules.EndMatch(new MatchEndContext(table.Match, JsonSerializer.SerializeToElement(state), TimeUp: true))!;

        Assert.Equal([1, 3, 1], Enumerable.Range(1, 3).Select(seat => ending.Standings.Single(s => s.PlayerId == table.Seat(seat)).Rank));
        Assert.Equal(140, ending.Standings.Single(s => s.PlayerId == table.Seat(1)).Result!.Value.GetProperty("mon").GetInt32());
        Assert.All(ending.State.Deserialize<RoyaleMatchState>(RoyaleTable.Json)!.Players, p => Assert.Null(p.Action));
    }

    public static TheoryData<string> UnplayableStates => new()
    {
        "not json object",
        """{"rulesVersion":9,"round":1,"nextCard":1,"players":[],"ledger":[]}""",
        """{"rulesVersion":1,"round":2,"nextCard":1,"players":[],"ledger":[]}""",
        """{"rulesVersion":1,"round":1,"nextCard":1,"players":[{"seat":1,"mon":-1,"hand":[]},{"seat":2,"mon":5,"hand":[]}],"ledger":[]}""",
        """{"rulesVersion":1,"round":1,"nextCard":1,"players":[{"seat":1,"mon":5,"hand":[{"id":"a","card":"Pump"},{"id":"b","card":"Pump"},{"id":"c","card":"Pump"},{"id":"d","card":"Pump"}]}],"ledger":[]}""",
        """{"rulesVersion":1,"round":1}""",
        """{"rulesVersion":1,"round":1,"event":42,"nextCard":1,"players":[],"ledger":[]}""",
    };

    [Theory]
    [MemberData(nameof(UnplayableStates))]
    public void A_state_this_server_cant_play_is_refused_and_left_as_it_is(string raw)
    {
        var table = new RoyaleTable(2);
        var state = raw.StartsWith('{') ? JsonDocument.Parse(raw).RootElement : JsonSerializer.SerializeToElement(raw);

        var decision = table.Rules.Apply(new MatchCommandContext(
            table.Match, table.Seat(1), ChogRoyaleMatchRules.SubmitAction, JsonSerializer.SerializeToElement(new { hodl = true }), state, table.Random));
        var resolution = table.Rules.ResolveRound(new MatchRoundContext(table.Match, state, table.Random));

        Rejected("game_state_unavailable", decision);
        Assert.Equal(raw.StartsWith('{') ? state.GetRawText() : "{}", resolution.State.GetRawText());
        Assert.Null(resolution.Standings);
    }

    [Fact]
    public void An_unreadable_state_still_ends_the_match_after_the_last_round_with_everyone_first()
    {
        var table = new RoyaleTable(2);
        for (var round = 1; round < 5; round++)
        {
            table.Resolve();
        }

        var resolution = table.Rules.ResolveRound(new MatchRoundContext(table.Match, null, table.Random));

        Assert.Equal("{}", resolution.State.GetRawText());
        Assert.All(resolution.Standings!, s => Assert.Equal(1, s.Rank));
        Assert.Null(table.Rules.ViewFor(new MatchViewContext(table.Match, table.Seat(1), JsonDocument.Parse("""{"rulesVersion":9}""").RootElement)));
    }
}
