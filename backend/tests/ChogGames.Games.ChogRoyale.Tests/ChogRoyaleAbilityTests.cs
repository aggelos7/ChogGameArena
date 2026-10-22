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
/// Chog Royale's abilities (STORY-080, ROYALE-039–044, DEC-091): each has its effect at the round's resolution, Alpha
/// Leak's preview reaches only its player, a used ability or one outside its window is refused unchanged, and guests and
/// holders are offered the same four with the same values.
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

    // Catalog and parity.

    [Fact]
    public void The_catalog_has_the_four_abilities_once_per_match_while_deciding()
    {
        var catalog = RoyaleAbilities.Catalog(new ChogRoyaleMatchRules([RoyaleRulesV1.Rules]));

        Assert.Equal(GameDefinition.ChogRoyale, catalog.GameType);
        Assert.Equal(
            [RoyaleAbilities.DiamondHandsPlus, RoyaleAbilities.AlphaLeak, RoyaleAbilities.DoublePump, RoyaleAbilities.StopLoss],
            catalog.Definitions.Select(d => d.Id));
        Assert.All(catalog.Definitions, d => Assert.Equal((1, AbilityTargetKind.None, RoyaleAbilities.Deciding), (d.MaxUses, d.Target, d.Windows.Single())));
    }

    [Fact]
    public void Guests_and_holders_of_any_tier_are_offered_the_same_abilities()
    {
        var rules = new ChogRoyaleMatchRules([RoyaleRulesV1.Rules]);
        var engine = new AbilityEngine([RoyaleAbilities.Catalog(rules)]);
        var holderRules = new HolderAbilityRules(
            "Tier",
            new Dictionary<string, HolderGameRules>
            {
                ["chog-life"] = new(new HashSet<string> { "reward_boost" }, new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyDictionary<string, decimal>>>()),
            });
        var pool = new AbilityPool(engine, new HolderAbilityStrategy(holderRules));
        var guest = CharacterSnapshot.Standard(DefaultAvatar.Classic, RoyaleTable.Start);
        var legendary = CharacterSnapshot.Restore(
            CharacterKind.Nft, "7", "Chog #7", null, [new NftTrait("Tier", "Legendary")], "0x00000000000000000000000000000000000000c7", RoyaleTable.Start);

        var offeredToGuest = pool.For(GameDefinition.ChogRoyale, guest);
        Assert.Equal(4, offeredToGuest.Count);
        Assert.Equal(offeredToGuest, pool.For(GameDefinition.ChogRoyale, legendary));
        Assert.All(offeredToGuest, a => Assert.False(pool.IsHolderOnly(GameDefinition.ChogRoyale, a.Id)));
    }
}
