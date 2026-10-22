using System.Text.Json;
using ChogGames.Core.Abilities;
using ChogGames.Core.Games;
using ChogGames.Core.Matches;
using ChogGames.Core.Nfts;
using ChogGames.Core.Players;
using ChogGames.Core.Rooms;
using ChogGames.Games.ChogRoyale.Abilities;
using ChogGames.Games.ChogRoyale.Rules;

namespace ChogGames.Games.ChogRoyale.Tests;

/// <summary>
/// Chog Royale's abilities (STORY-080, STORY-093, ROYALE-039–044, DEC-091, DEC-095): each has its effect at the round's
/// resolution, Alpha Leak's preview, Fresh Mint's hand and Insider Info's look reach only their player, a used ability or
/// one outside its window is refused unchanged, Tier values and uses apply, and holders are offered all six abilities
/// where guests get the two standard ones.
/// </summary>
public class ChogRoyaleAbilityTests
{
    // Deal draws: 0 Pump, 30 Rug Pull, 50 Diamond Hands, 65 Whale Swap.
    private const int Pump = 0;
    private const int Rug = 30;
    private const int Diamond = 50;
    private const int Whale = 65;

    // Event draws: 0 Calm Market, 30 Meme Hype, 50 Market Crash.
    private const int Calm = 0;
    private const int Hype = 30;
    private const int Crash = 50;

    /// <summary>A table whose seats are each dealt three of the given card and lock the given abilities; round 1 is Calm.</summary>
    private static RoyaleTable Dealt(string?[] abilities, params int[] cards) =>
        new(cards.Length, new Draws([.. cards.SelectMany(card => new[] { card, card, card }), Calm]), abilities: abilities);

    private static Func<RoyaleMatchState, RoyaleMatchState> Holding(params int[] mon) =>
        state => state with { Players = [.. state.Players.Select(p => p with { Mon = mon[p.Seat - 1] })] };

    private static RoyaleLedgerEntry Entry(RoyaleTable table, int seat) =>
        table.Stored.Ledger[^1].Entries.Single(e => e.Seat == seat);

    private static int[] Balances(RoyaleTable table) => [.. table.Stored.Players.OrderBy(p => p.Seat).Select(p => p.Mon)];

    private static int? UsesLeft(RoyaleTable table, int seat) => table.Match.Members.Single(m => m.Seat == seat).AbilityUsesLeft;

    // Diamond Hands+.

    [Fact]
    public void Diamond_hands_plus_blocks_an_attack_without_a_diamond_hands_card()
    {
        var table = Dealt([null, RoyaleAbilities.DiamondHandsPlus], Rug, Pump);
        Assert.Null(table.UseAbility(2, RoyaleAbilities.DiamondHandsPlus));
        table.Play(1, 0, target: 2);

        table.Resolve();

        Assert.Equal([100, 100], Balances(table));
        Assert.Equal(RoyaleOutcome.Blocked, Entry(table, 1).Outcome);
        Assert.Equal((RoyaleDefense.Blocked, RoyaleAbilities.DiamondHandsPlus, 0), (Entry(table, 2).Defense!.Value, Entry(table, 2).Ability, Entry(table, 2).Change));
        Assert.Equal(0, UsesLeft(table, 2));
    }

    [Fact]
    public void Diamond_hands_plus_blocks_one_more_attack_than_the_card()
    {
        var table = Dealt([null, null, RoyaleAbilities.DiamondHandsPlus, null], Rug, Rug, Diamond, Rug);
        Assert.Null(table.UseAbility(3, RoyaleAbilities.DiamondHandsPlus));
        table.Play(1, 0, target: 3);
        table.Play(2, 0, target: 3);
        table.Play(3, 0);
        table.Play(4, 0, target: 3);

        table.Resolve();

        // The card and the ability block the first two Rugs in priority; the third lands on 105.
        Assert.Equal([RoyaleOutcome.Blocked, RoyaleOutcome.Blocked, RoyaleOutcome.Applied], new[] { 1, 2, 4 }.Select(seat => Entry(table, seat).Outcome!.Value));
        Assert.Equal([100, 100, 90, 115], Balances(table));
    }

    [Fact]
    public void An_unused_diamond_hands_plus_shows_as_raised()
    {
        var table = Dealt([RoyaleAbilities.DiamondHandsPlus, null], Pump, Pump);
        Assert.Null(table.UseAbility(1, RoyaleAbilities.DiamondHandsPlus));

        table.Resolve();

        Assert.Equal(RoyaleDefense.Raised, Entry(table, 1).Defense);
        Assert.Equal([100, 100], Balances(table));
    }

    // Alpha Leak.

    [Fact]
    public void Alpha_leak_shows_the_next_rounds_event_only_to_its_player_and_the_next_round_plays_it()
    {
        var table = Dealt([RoyaleAbilities.AlphaLeak, null], Pump, Pump);
        ((Draws)table.Random).Enqueue(Hype);

        Assert.Null(table.UseAbility(1, RoyaleAbilities.AlphaLeak));

        var mine = table.ViewOf(1);
        Assert.Equal(("CalmMarket", "MemeHype", RoyaleAbilities.AlphaLeak), (mine.GetProperty("event").GetString(), mine.GetProperty("nextEvent").GetString(), mine.GetProperty("ability").GetString()));
        var theirs = table.ViewOf(2);
        Assert.False(theirs.TryGetProperty("nextEvent", out _));
        Assert.False(theirs.TryGetProperty("ability", out _));
        Assert.DoesNotContain("MemeHype", theirs.GetRawText(), StringComparison.Ordinal);

        // Round 1 draws no event when it resolves: round 2 is the leaked Meme Hype, and the leak is spent.
        ((Draws)table.Random).Enqueue(Crash);
        table.Resolve();

        Assert.Equal(RoyaleMarketEvent.MemeHype, table.Stored.Event);
        Assert.Null(table.Stored.NextEvent);
        Assert.False(table.ViewOf(1).TryGetProperty("nextEvent", out _));
        Assert.Equal(RoyaleAbilities.AlphaLeak, Entry(table, 1).Ability);
    }

    [Fact]
    public void Alpha_leak_is_refused_in_the_last_round()
    {
        var table = Dealt([RoyaleAbilities.AlphaLeak, null], Pump, Pump);
        for (var round = 1; round < 5; round++)
        {
            table.Resolve();
        }

        Assert.Equal("last_round", table.UseAbility(1, RoyaleAbilities.AlphaLeak));
        Assert.Equal(1, UsesLeft(table, 1));
    }

    // Double Pump.

    [Fact]
    public void Double_pump_adds_half_to_a_pump()
    {
        var table = Dealt([RoyaleAbilities.DoublePump, null], Pump, Pump);
        Assert.Null(table.UseAbility(1, RoyaleAbilities.DoublePump));
        table.Play(1, 0);
        table.Play(2, 0);

        table.Resolve();

        Assert.Equal([130, 120], Balances(table));
        Assert.Equal((30, RoyaleAbilities.DoublePump), (Entry(table, 1).Amount, Entry(table, 1).Ability));
    }

    [Fact]
    public void Double_pump_stacks_with_meme_hype_and_rounds_down_once()
    {
        var table = Dealt([RoyaleAbilities.DoublePump, null], Pump, Pump);
        Assert.Null(table.UseAbility(1, RoyaleAbilities.DoublePump));
        table.Play(1, 0);

        table.Resolve(state => state with { Event = RoyaleMarketEvent.MemeHype });

        // 20 × 150% × 150% = 45.
        Assert.Equal(145, table.Mon(1));
    }

    [Fact]
    public void Double_pump_is_spent_without_effect_when_no_pump_is_played()
    {
        var table = Dealt([RoyaleAbilities.DoublePump, null], Pump, Pump);
        Assert.Null(table.UseAbility(1, RoyaleAbilities.DoublePump));
        table.Hodl(1);

        table.Resolve();
        table.Play(1, 0);
        table.Resolve();

        Assert.Equal(120, table.Mon(1));
        Assert.Equal(0, UsesLeft(table, 1));
    }

    [Fact]
    public void Double_pump_is_refused_without_a_pump_in_hand()
    {
        var table = Dealt([RoyaleAbilities.DoublePump, null], Rug, Pump);

        Assert.Equal("no_pump", table.UseAbility(1, RoyaleAbilities.DoublePump));
        Assert.Equal(1, UsesLeft(table, 1));
        Assert.Null(table.Stored.Players[0].Ability);
    }

    // Stop Loss.

    [Fact]
    public void Stop_loss_caps_what_rug_pulls_take()
    {
        var table = Dealt([null, null, RoyaleAbilities.StopLoss], Rug, Rug, Pump);
        Assert.Null(table.UseAbility(3, RoyaleAbilities.StopLoss));
        table.Play(1, 0, target: 3);
        table.Play(2, 0, target: 3);

        table.Resolve();

        // At most 5 in all: the first Rug in priority takes 5, the second nothing.
        Assert.Equal([105, 100, 95], Balances(table));
        Assert.Equal((RoyaleOutcome.Applied, 5), (Entry(table, 1).Outcome!.Value, Entry(table, 1).Amount));
        Assert.Equal((RoyaleOutcome.Stopped, 0), (Entry(table, 2).Outcome!.Value, Entry(table, 2).Amount));
        Assert.Equal(5, Entry(table, 3).Lost);
    }

    [Fact]
    public void Stop_loss_caps_a_swaps_net_loss()
    {
        var table = Dealt([null, RoyaleAbilities.StopLoss], Whale, Pump);
        Assert.Null(table.UseAbility(2, RoyaleAbilities.StopLoss));
        table.Play(1, 0, target: 2);

        table.Resolve(Holding(20, 100));

        // Seat 1 gives 4 and would get 20; seat 2's net loss stays at 5.
        Assert.Equal([25, 95], Balances(table));
        Assert.Equal(5, Entry(table, 2).Lost);
    }

    // Uses and windows.

    [Fact]
    public void An_ability_is_used_once_per_match()
    {
        var table = Dealt([RoyaleAbilities.StopLoss, null], Pump, Pump);
        Assert.Null(table.UseAbility(1, RoyaleAbilities.StopLoss));
        var version = table.Match.Version;

        Assert.Equal(AbilityRefusal.UsedUp, table.UseAbility(1, RoyaleAbilities.StopLoss));
        table.Resolve();
        Assert.Equal(AbilityRefusal.UsedUp, table.UseAbility(1, RoyaleAbilities.StopLoss));
        Assert.Equal(version + 1, table.Match.Version);
    }

    [Fact]
    public void An_ability_is_refused_after_the_action_is_locked_and_before_the_round_opens()
    {
        var table = Dealt([RoyaleAbilities.StopLoss, RoyaleAbilities.StopLoss, null], Pump, Pump, Pump);
        table.Hodl(1, locks: true);

        Assert.Equal("ActionLocked", table.UseAbility(1, RoyaleAbilities.StopLoss));

        table.Resolve();
        Assert.Equal(1, UsesLeft(table, 1));
        var opens = table.Match.RoundOpensAt!.Value;
        var early = new MatchCommand(Guid.NewGuid(), table.Match.Id, table.Match.Version, MatchCommand.UseAbility, JsonSerializer.SerializeToElement(new { abilityId = RoyaleAbilities.StopLoss }));
        Assert.Equal(MatchCommandProblem.RoundNotOpen, table.Match.CheckCommand(table.Seat(2), early, opens.AddSeconds(-1)));
    }

    [Fact]
    public void Another_players_ability_and_an_unlocked_one_are_refused()
    {
        var table = Dealt([RoyaleAbilities.StopLoss, null], Pump, Pump);

        Assert.Equal(AbilityRefusal.NotYourAbility, table.UseAbility(1, RoyaleAbilities.AlphaLeak));
        Assert.Equal(AbilityRefusal.NotYourAbility, table.UseAbility(2, RoyaleAbilities.StopLoss));
    }

    // Fresh Mint.

    [Fact]
    public void Fresh_mint_deals_a_new_hand_that_only_its_player_sees()
    {
        var table = Dealt([RoyaleAbilities.FreshMint, null], Pump, Pump);
        ((Draws)table.Random).Enqueue(Rug, Diamond, Whale);

        Assert.Null(table.UseAbility(1, RoyaleAbilities.FreshMint));

        Assert.Equal(
            [("c7", RoyaleCard.RugPull), ("c8", RoyaleCard.DiamondHands), ("c9", RoyaleCard.WhaleSwap)],
            table.Hand(1).Select(c => (c.Id, c.Card)));
        Assert.Equal(10, table.Stored.NextCard);
        Assert.Equal(["c7", "c8", "c9"], table.ViewOf(1).GetProperty("hand").EnumerateArray().Select(c => c.GetProperty("id").GetString()));
        var theirs = table.ViewOf(2);
        Assert.DoesNotContain("c7", theirs.GetRawText(), StringComparison.Ordinal);
        Assert.False(theirs.TryGetProperty("ability", out _));
        Assert.Equal(3, theirs.GetProperty("players")[0].GetProperty("cards").GetInt32());

        table.Play(1, 0, target: 2);
        table.Resolve();

        Assert.Equal([115, 85], Balances(table));
        Assert.Equal(RoyaleAbilities.FreshMint, Entry(table, 1).Ability);
    }

    [Fact]
    public void Fresh_mint_drops_an_unlocked_card_choice_and_the_old_cards_are_gone()
    {
        var table = Dealt([RoyaleAbilities.FreshMint, RoyaleAbilities.FreshMint], Pump, Pump);
        var old = table.Hand(1)[0].Id;
        table.Play(1, 0);
        table.Hodl(2);

        Assert.Null(table.UseAbility(1, RoyaleAbilities.FreshMint));
        Assert.Null(table.UseAbility(2, RoyaleAbilities.FreshMint));

        Assert.Null(table.Stored.Players[0].Action);
        Assert.Equal(new RoyaleAction(Hodl: true), table.Stored.Players[1].Action);
        var refused = table.Rules.Apply(new MatchCommandContext(
            table.Match, table.Seat(1), ChogRoyaleMatchRules.SubmitAction, JsonSerializer.SerializeToElement(new { card = old }), table.State, table.Random));
        Assert.Equal("card_not_in_hand", refused.Reason);
    }

    // Insider Info.

    [Fact]
    public void Insider_info_shows_the_opponents_hand_but_not_their_action_to_its_player_only()
    {
        var table = Dealt([RoyaleAbilities.InsiderInfo, null, null], Pump, Rug, Pump);
        table.Play(2, 0, target: 3);

        Assert.Null(table.UseAbility(1, RoyaleAbilities.InsiderInfo, target: 2));

        var insider = table.ViewOf(1).GetProperty("insider");
        Assert.Equal(2, insider.GetProperty("seat").GetInt32());
        Assert.Equal(table.Hand(2).Select(c => c.Id), insider.GetProperty("hand").EnumerateArray().Select(c => c.GetProperty("id").GetString()));
        Assert.DoesNotContain("target", insider.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("action", insider.GetRawText(), StringComparison.OrdinalIgnoreCase);
        foreach (var seat in new[] { 2, 3 })
        {
            var view = table.ViewOf(seat);
            Assert.False(view.TryGetProperty("insider", out _));
            Assert.False(view.TryGetProperty("ability", out _));
        }

        Assert.DoesNotContain(table.Hand(2)[1].Id, table.ViewOf(3).GetRawText(), StringComparison.Ordinal);

        table.Resolve();

        Assert.Equal((RoyaleAbilities.InsiderInfo, 2), (Entry(table, 1).Ability, Entry(table, 1).AbilityTarget!.Value));
        Assert.Null(Entry(table, 2).AbilityTarget);
        Assert.False(table.ViewOf(1).TryGetProperty("insider", out _));
    }

    [Fact]
    public void Insider_info_shows_a_hand_its_opponent_mints_afterwards()
    {
        var table = Dealt([RoyaleAbilities.InsiderInfo, RoyaleAbilities.FreshMint], Pump, Pump);
        Assert.Null(table.UseAbility(1, RoyaleAbilities.InsiderInfo, target: 2));
        ((Draws)table.Random).Enqueue(Rug, Rug, Rug);

        Assert.Null(table.UseAbility(2, RoyaleAbilities.FreshMint));

        Assert.Equal(["c7", "c8", "c9"], table.ViewOf(1).GetProperty("insider").GetProperty("hand").EnumerateArray().Select(c => c.GetProperty("id").GetString()));
    }

    [Fact]
    public void Insider_info_is_refused_on_the_player_a_player_who_left_or_without_a_target()
    {
        var table = Dealt([RoyaleAbilities.InsiderInfo, null, null], Pump, Pump, Pump);
        table.Match.Leave(table.Seat(3), table.Now);
        var version = table.Match.Version;

        Assert.Equal(AbilityRefusal.InvalidTarget, table.UseAbility(1, RoyaleAbilities.InsiderInfo, target: 1));
        Assert.Equal(AbilityRefusal.InvalidTarget, table.UseAbility(1, RoyaleAbilities.InsiderInfo, target: 3));
        Assert.Equal(AbilityRefusal.InvalidTarget, table.UseAbility(1, RoyaleAbilities.InsiderInfo));
        Assert.Equal(AbilityRefusal.InvalidTarget, table.UseAbility(1, RoyaleAbilities.InsiderInfo, target: 9));
        Assert.Equal((version, 1), (table.Match.Version, UsesLeft(table, 1)));
    }

    [Fact]
    public void Insider_info_stops_showing_an_opponent_who_leaves()
    {
        var table = Dealt([RoyaleAbilities.InsiderInfo, null, null], Pump, Pump, Pump);
        Assert.Null(table.UseAbility(1, RoyaleAbilities.InsiderInfo, target: 2));

        table.Match.Leave(table.Seat(2), table.Now);

        Assert.False(table.ViewOf(1).TryGetProperty("insider", out _));
    }

    // Tier values and uses (DEC-077, DEC-095).

    private static Func<AbilityDefinition, AbilityDefinition> Tier(string name, decimal value) =>
        ability => ability.WithValues(new Dictionary<string, decimal> { [name] = value }, ability.MaxUses);

    private static AbilityDefinition Twice(AbilityDefinition ability) => ability.WithValues(ability.Parameters, 2);

    [Fact]
    public void A_legendary_stop_loss_lets_nothing_through()
    {
        var table = new RoyaleTable(2, new Draws([Rug, Rug, Rug, Pump, Pump, Pump, Calm]), abilities: [null, RoyaleAbilities.StopLoss], tier: Tier(RoyaleAbilities.Mon, 0));
        Assert.Null(table.UseAbility(2, RoyaleAbilities.StopLoss));
        table.Play(1, 0, target: 2);

        table.Resolve();

        Assert.Equal([100, 100], Balances(table));
        Assert.Equal(RoyaleOutcome.Stopped, Entry(table, 1).Outcome);
    }

    [Fact]
    public void A_legendary_double_pump_doubles_a_pump()
    {
        var table = new RoyaleTable(2, new Draws([Pump, Pump, Pump, Pump, Pump, Pump, Calm]), abilities: [RoyaleAbilities.DoublePump], tier: Tier(RoyaleAbilities.Percent, 100));
        Assert.Null(table.UseAbility(1, RoyaleAbilities.DoublePump));
        table.Play(1, 0);

        table.Resolve();

        Assert.Equal(140, table.Mon(1));
    }

    [Fact]
    public void A_legendary_diamond_hands_plus_blocks_three_attacks()
    {
        var table = new RoyaleTable(4, new Draws([.. Enumerable.Repeat(Rug, 9), Pump, Pump, Pump, Calm]), abilities: [null, null, null, RoyaleAbilities.DiamondHandsPlus], tier: Tier(RoyaleAbilities.Blocks, 3));
        Assert.Null(table.UseAbility(4, RoyaleAbilities.DiamondHandsPlus));
        foreach (var seat in new[] { 1, 2, 3 })
        {
            table.Play(seat, 0, target: 4);
        }

        table.Resolve();

        Assert.All(new[] { 1, 2, 3 }, seat => Assert.Equal(RoyaleOutcome.Blocked, Entry(table, seat).Outcome));
        Assert.Equal(100, table.Mon(4));
    }

    [Theory]
    [InlineData(RoyaleAbilities.FreshMint)]
    [InlineData(RoyaleAbilities.AlphaLeak)]
    [InlineData(RoyaleAbilities.InsiderInfo)]
    public void An_epic_ability_with_two_uses_is_used_once_a_round(string ability)
    {
        var table = new RoyaleTable(2, new Draws([Pump, Pump, Pump, Pump, Pump, Pump, Calm]), abilities: [ability], tier: Twice);
        int? target = ability == RoyaleAbilities.InsiderInfo ? 2 : null;

        Assert.Null(table.UseAbility(1, ability, target));
        Assert.Equal(AbilityRefusal.NotNow, table.UseAbility(1, ability, target));
        table.Resolve();
        Assert.Null(table.UseAbility(1, ability, target));
        Assert.Equal(0, UsesLeft(table, 1));
        table.Resolve();
        Assert.Equal(AbilityRefusal.UsedUp, table.UseAbility(1, ability, target));
        Assert.Equal([ability, ability], table.Stored.Ledger.Select(round => round.Entries[0].Ability));
    }

    // Catalog and holders.

    [Fact]
    public void The_catalog_has_six_abilities_once_per_match_while_deciding()
    {
        var catalog = RoyaleAbilities.Catalog(new ChogRoyaleMatchRules([RoyaleRulesV1.Rules]));

        Assert.Equal((GameDefinition.ChogRoyale, 2), (catalog.GameType, catalog.Version));
        Assert.Equal(
            [
                RoyaleAbilities.DiamondHandsPlus, RoyaleAbilities.AlphaLeak, RoyaleAbilities.DoublePump, RoyaleAbilities.StopLoss,
                RoyaleAbilities.FreshMint, RoyaleAbilities.InsiderInfo,
            ],
            catalog.Definitions.Select(d => d.Id));
        Assert.All(catalog.Definitions, d => Assert.Equal((1, RoyaleAbilities.Deciding), (d.MaxUses, d.Windows.Single())));
        Assert.Equal(
            [RoyaleAbilities.InsiderInfo],
            catalog.Definitions.Where(d => d.Target == AbilityTargetKind.Opponent).Select(d => d.Id));
    }

    [Fact]
    public void Guests_get_the_two_standard_abilities_and_holders_all_six_with_their_tiers_values()
    {
        var rules = new ChogRoyaleMatchRules([RoyaleRulesV1.Rules]);
        var engine = new AbilityEngine([RoyaleAbilities.Catalog(rules)]);
        var holderRules = new HolderAbilityRules(
            "Tier",
            new Dictionary<string, HolderGameRules>
            {
                [GameDefinition.ChogRoyale] = new(
                    new HashSet<string> { RoyaleAbilities.DiamondHandsPlus, RoyaleAbilities.StopLoss },
                    new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyDictionary<string, decimal>>>
                    {
                        ["legendary"] = new Dictionary<string, IReadOnlyDictionary<string, decimal>>
                        {
                            [RoyaleAbilities.StopLoss] = new Dictionary<string, decimal> { [RoyaleAbilities.Mon] = 0 },
                            [RoyaleAbilities.FreshMint] = new Dictionary<string, decimal> { [AbilityDefinition.Uses] = 2 },
                        },
                    }),
            });
        var pool = new AbilityPool(engine, new HolderAbilityStrategy(holderRules));
        var guest = CharacterSnapshot.Standard(DefaultAvatar.Classic, RoyaleTable.Start);
        var legendary = CharacterSnapshot.Restore(
            CharacterKind.Nft, "7", "Chog #7", null, [new NftTrait("Tier", "Legendary")], "0x00000000000000000000000000000000000000c7", RoyaleTable.Start);

        var offeredToGuest = pool.For(GameDefinition.ChogRoyale, guest);
        var offeredToHolder = pool.For(GameDefinition.ChogRoyale, legendary);

        Assert.Equal([RoyaleAbilities.DiamondHandsPlus, RoyaleAbilities.StopLoss], offeredToGuest.Select(a => a.Id));
        Assert.Equal(5m, offeredToGuest.Single(a => a.Id == RoyaleAbilities.StopLoss).Parameters[RoyaleAbilities.Mon]);
        Assert.Equal(6, offeredToHolder.Count);
        Assert.Equal(0m, offeredToHolder.Single(a => a.Id == RoyaleAbilities.StopLoss).Parameters[RoyaleAbilities.Mon]);
        Assert.Equal(2, offeredToHolder.Single(a => a.Id == RoyaleAbilities.FreshMint).MaxUses);
        Assert.Equal(
            [RoyaleAbilities.AlphaLeak, RoyaleAbilities.DoublePump, RoyaleAbilities.FreshMint, RoyaleAbilities.InsiderInfo],
            offeredToHolder.Where(a => pool.IsHolderOnly(GameDefinition.ChogRoyale, a.Id)).Select(a => a.Id));
    }
}
