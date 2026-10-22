using System.Text.Json;
using ChogGames.Core.Abilities;
using ChogGames.Core.Games;

namespace ChogGames.Games.ChogRoyale.Abilities;

/// <summary>
/// Chog Royale's abilities (STORY-080, ROYALE-039–044, DEC-091), one of which each player locks for a match and uses once,
/// while the round's decisions are open and before they lock their action. Using one is private: others learn of it at
/// the round's reveal, where the ledger shows it.
/// <list type="bullet">
/// <item>Diamond Hands+ raises a shield for the round that blocks <c>blocks</c> more hostile cards aimed at its player (1),
/// besides any Diamond Hands card they play, with no MON of its own.</item>
/// <item>Alpha Leak draws the next round's market event now, and only its player sees it until that round's announcement.
/// Not in the last round.</item>
/// <item>Double Pump adds <c>percent</c> (50) to the player's Pump gain this round, with the round's modifiers, rounded down
/// once. Used while holding a Pump; it is spent whether or not a Pump is played.</item>
/// <item>Stop Loss: other players' cards take at most <c>mon</c> MON (5) from its player this round, Rug Pulls first in
/// resolution order, then swaps. It replaces <c>last_second_exit</c>, since actions are revisable until the lock.</item>
/// </list>
/// Guests and holders choose from all four with the same values (ROYALE-037, ROYALE-044): Chog Royale isn't in
/// <c>Abilities:Holder</c>, so the holder strategy offers everyone the whole catalog.
/// </summary>
public static class RoyaleAbilities
{
    public const int Version = 1;

    public const string DiamondHandsPlus = "diamond_hands_plus";
    public const string AlphaLeak = "alpha_leak";
    public const string DoublePump = "double_pump";
    public const string StopLoss = "stop_loss";

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

    /// <summary>The catalog, version 1, whose handlers act on <paramref name="game"/>'s match states.</summary>
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
            "Use while deciding, before the last round. Only you see the next round's market event, a round early.",
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
