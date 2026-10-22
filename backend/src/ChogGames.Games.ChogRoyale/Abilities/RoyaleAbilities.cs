using System.Text.Json;
using ChogGames.Core.Abilities;
using ChogGames.Core.Games;

namespace ChogGames.Games.ChogRoyale.Abilities;

/// <summary>
/// Chog Royale's abilities (STORY-080, STORY-093, ROYALE-039–044, DEC-091, DEC-095), one of which each player locks for a
/// match and uses once, or as often as their Tier allows but once a round, while the round's decisions are open and before
/// they lock their action. Using one is private: others learn of it at the round's reveal, where the ledger shows it.
/// <list type="bullet">
/// <item>Diamond Hands+ raises a shield for the round that blocks <c>blocks</c> more hostile cards aimed at its player (1),
/// besides any Diamond Hands card they play, with no MON of its own.</item>
/// <item>Alpha Leak draws the next round's market event now, and only its player sees it until that round's announcement.
/// Not in the last round.</item>
/// <item>Double Pump adds <c>percent</c> (50) to the player's Pump gain this round, with the round's modifiers, rounded down
/// once. Used while holding a Pump; it is spent whether or not a Pump is played.</item>
/// <item>Stop Loss: other players' cards take at most <c>mon</c> MON (5) from its player this round, Rug Pulls first in
/// resolution order, then swaps. It replaces <c>last_second_exit</c>, since actions are revisable until the lock.</item>
/// <item>Fresh Mint replaces its player's whole hand at once with new cards from the rules version's weighted deck, with
/// match-unique ids, which only they see; a card they had chosen and not locked is dropped (STORY-093, DEC-095).</item>
/// <item>Insider Info, on an opponent still playing: its player sees that opponent's hand as it is, never their action,
/// for the rest of the round; the reveal shows whom they looked at (STORY-093, DEC-095).</item>
/// </list>
/// The values here are the Base Tier's. Version 2 (STORY-093, DEC-095) added Fresh Mint and Insider Info: Diamond Hands+
/// and Stop Loss are standard, the other four holder-only, and rarer Tiers lock stronger values from
/// <c>Abilities:Holder</c> (DEC-077), which replaced version 1's parity between guests and holders (ROYALE-037).
/// </summary>
public static class RoyaleAbilities
{
    public const int Version = 2;

    public const string DiamondHandsPlus = "diamond_hands_plus";
    public const string AlphaLeak = "alpha_leak";
    public const string DoublePump = "double_pump";
    public const string StopLoss = "stop_loss";
    public const string FreshMint = "fresh_mint";
    public const string InsiderInfo = "insider_info";

    /// <summary>The round's decision window, before the player locks their action.</summary>
    public const string Deciding = "deciding";

    /// <summary>The extra attacks Diamond Hands+ blocks; 1 to <see cref="MaxBlocks"/>.</summary>
    public const string Blocks = "blocks";

    /// <summary>What Double Pump adds to a Pump's gain, in percent; 1 to <see cref="MaxPercent"/>.</summary>
    public const string Percent = "percent";

    /// <summary>The most MON Stop Loss lets other players' cards take; 0 to <see cref="MaxStopLoss"/>.</summary>
    public const string Mon = "mon";

    public const int MaxBlocks = 3;

    public const int MaxPercent = 200;

    public const int MaxStopLoss = 50;

    /// <summary>The catalog, version 2, whose handlers act on <paramref name="game"/>'s match states.</summary>
    public static AbilityCatalog Catalog(ChogRoyaleMatchRules game)
    {
        ArgumentNullException.ThrowIfNull(game);
        return AbilityCatalog.Create(
            GameDefinition.ChogRoyale,
            Version,
            Definitions(),
            [
                new Handler(game, DiamondHandsPlus, [new AbilityParameter(Blocks, 1, MaxBlocks)]),
                new Handler(game, AlphaLeak, []),
                new Handler(game, DoublePump, [new AbilityParameter(Percent, 1, MaxPercent)]),
                new Handler(game, StopLoss, [new AbilityParameter(Mon, 0, MaxStopLoss)]),
                new Handler(game, FreshMint, []),
                new Handler(game, InsiderInfo, []),
            ],
            new Timing(game));
    }

    private static IEnumerable<AbilityDefinition> Definitions() =>
    [
        AbilityDefinition.Create(
            DiamondHandsPlus,
            "Diamond Hands+",
            "Use while deciding. This round, a shield blocks {blocks|the first attack|the first # attacks} on you, on top of any Diamond Hands you play.",
            AbilityTargetKind.None,
            [Deciding],
            new Dictionary<string, decimal> { [Blocks] = 1 }),
        AbilityDefinition.Create(
            AlphaLeak,
            "Alpha Leak",
            "Use while deciding, before the last round. Only you see the next round's market event, a round early. {uses|1 use|# uses} per game, one a round.",
            AbilityTargetKind.None,
            [Deciding]),
        AbilityDefinition.Create(
            DoublePump,
            "Double Pump",
            "Use while deciding, holding a Pump. Your Pump this round gains {percent}% more.",
            AbilityTargetKind.None,
            [Deciding],
            new Dictionary<string, decimal> { [Percent] = 50 }),
        AbilityDefinition.Create(
            StopLoss,
            "Stop Loss",
            "Use while deciding. This round, other players' cards take at most {mon} MON from you.",
            AbilityTargetKind.None,
            [Deciding],
            new Dictionary<string, decimal> { [Mon] = 5 }),
        AbilityDefinition.Create(
            FreshMint,
            "Fresh Mint",
            "Use while deciding. Your whole hand is swapped for new cards, which only you see. {uses|1 use|# uses} per game, one a round.",
            AbilityTargetKind.None,
            [Deciding]),
        AbilityDefinition.Create(
            InsiderInfo,
            "Insider Info",
            "Use while deciding. Only you see an opponent's hand for the rest of the round; they learn you looked at the reveal. {uses|1 use|# uses} per game, one a round.",
            AbilityTargetKind.Opponent,
            [Deciding]),
    ];

    private sealed class Handler(ChogRoyaleMatchRules game, string abilityId, IReadOnlyList<AbilityParameter> parameters) : IAbilityHandler
    {
        public string AbilityId => abilityId;

        public IReadOnlyList<AbilityParameter> Parameters => parameters;

        public string? CanExecute(AbilityContext context) => game.CanUse(context);

        public JsonElement Execute(AbilityContext context) => game.Use(context);
    }

    private sealed class Timing(ChogRoyaleMatchRules game) : IAbilityTiming
    {
        public string? CurrentWindow(AbilityContext context) => game.AbilityWindow(context);
    }
}
