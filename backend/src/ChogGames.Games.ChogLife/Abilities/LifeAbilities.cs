using System.Text.Json;
using ChogGames.Core.Abilities;
using ChogGames.Core.Games;
using ChogGames.Games.ChogLife.Rules;

namespace ChogGames.Games.ChogLife.Abilities;

/// <summary>
/// Chog Life's abilities (LIFE-008, STORY-042, DEC-044), one of which each player locks for a match and uses once.
/// <list type="bullet">
/// <item>Reward Boost, Loss Protection and Lucky Reroll are used on the player's turn before they roll. Each stays armed
/// until the first tile outcome it fits, which it changes, then it is spent: a gain from a Reward tile or an event earns
/// <c>percent</c> more as bonuses (25% at Base), only <c>percent</c> of a loss from a Loss tile or an event is taken (50%
/// at Base), and an event with several outcomes is drawn <c>draws</c> times (2 at Base) and the best one for the player
/// applies. Fork stakes and the finish bonus are never changed.</item>
/// <item>Event Preview is used at a fork before choosing a lane. Choosing then draws the next move on that lane and shows
/// only that player the die, the tile and the event outcome there (never a stake's payout). In the same turn they keep
/// the lane, and the roll plays out exactly as shown, or switch to the other lane blind, and the next roll is drawn
/// afresh. A turn that runs out keeps the lane. Holders of rarer Tiers may use it more than once.</item>
/// <item>Double Dice is used on the player's turn before they roll: the server rolls <c>dice</c> dice (2 at Base) at once,
/// which every player sees, and the player's roll moves by the one they keep. A turn that runs out first keeps the
/// highest, for their next roll (STORY-091, DEC-078).</item>
/// <item>Tax the Rich is used on the player's turn before they roll: it takes <c>percent</c> (10% at Base) of the liquid MON
/// of the opponent with the highest total portfolio, at most <c>cap</c> MON, and adds it to the player's liquid MON.
/// Every opponent who hasn't left counts, finished or not; a tie takes from the first by seat. A player whose total is
/// already the highest can't use it (STORY-091, DEC-078).</item>
/// </list>
/// The values here are the Base Tier's; holders of rarer Tiers lock stronger ones from <c>Abilities:Holder</c> (STORY-090,
/// DEC-077). Version 2 added <c>draws</c> and made <c>percent</c> of Loss Protection the share of a loss taken (unchanged at
/// 50%). Version 3 added Double Dice and Tax the Rich, which only holders may choose.
/// </summary>
public static class LifeAbilities
{
    public const int Version = 3;

    public const string RewardBoost = "reward_boost";
    public const string LossProtection = "loss_protection";
    public const string EventPreview = "event_preview";
    public const string EventReroll = "event_reroll";
    public const string DoubleDice = "double_dice";
    public const string TaxTheRich = "tax_the_rich";

    /// <summary>The player's turn, before they roll: not while choosing a lane, and not once they have finished.</summary>
    public const string BeforeRoll = "before_roll";

    /// <summary>The player's turn, while they choose a lane at a fork their roll stopped on.</summary>
    public const string AtFork = "at_fork";

    /// <summary>The share of a gain added, or of a loss still taken, in percent; a whole number of 1 to 100.</summary>
    public const string Percent = "percent";

    /// <summary>How many times Lucky Reroll draws an event, the best kept; 2 to <see cref="MaxDraws"/>.</summary>
    public const string Draws = "draws";

    public const int MaxDraws = 5;

    /// <summary>How many dice Double Dice rolls, one kept; 2 to <see cref="MaxDice"/>.</summary>
    public const string Dice = "dice";

    public const int MaxDice = 4;

    /// <summary>The most MON one Tax the Rich takes; 1 to <see cref="MaxCap"/>.</summary>
    public const string Cap = "cap";

    public const int MaxCap = LifeRules.MaxTileAmount;

    /// <summary>The catalog, version 3, whose handlers act on <paramref name="game"/>'s match states.</summary>
    public static AbilityCatalog Catalog(ChogLifeMatchRules game)
    {
        ArgumentNullException.ThrowIfNull(game);
        return AbilityCatalog.Create(
            GameDefinition.ChogLife,
            Version,
            Definitions(),
            [
                new ArmHandler(game, RewardBoost, [new AbilityParameter(Percent, 1, 100)]),
                new ArmHandler(game, LossProtection, [new AbilityParameter(Percent, 1, 100)]),
                new ArmHandler(game, EventReroll, [new AbilityParameter(Draws, 2, MaxDraws, Default: 2)]),
                new ArmHandler(game, EventPreview, []),
                new DiceHandler(game),
                new TaxHandler(game),
            ],
            new Timing(game));
    }

    private static IEnumerable<AbilityDefinition> Definitions() =>
    [
        AbilityDefinition.Create(
            RewardBoost,
            "Reward Boost",
            "Use before you roll. Your next gain from a reward tile or an event earns {percent}% more, added to your bonuses.",
            AbilityTargetKind.None,
            [BeforeRoll],
            new Dictionary<string, decimal> { [Percent] = 25 }),
        AbilityDefinition.Create(
            LossProtection,
            "Loss Protection",
            "Use before you roll. You take only {percent}% of your next loss from a loss tile or an event.",
            AbilityTargetKind.None,
            [BeforeRoll],
            new Dictionary<string, decimal> { [Percent] = 50 }),
        AbilityDefinition.Create(
            EventPreview,
            "Event Preview",
            "Use at a fork, then choose a route: only you see what your next roll does there. Keep it, or switch to the other route blind. {uses|1 use|# uses} per game.",
            AbilityTargetKind.None,
            [AtFork]),
        AbilityDefinition.Create(
            EventReroll,
            "Lucky Reroll",
            "Use before you roll. Your next event with several outcomes is drawn {draws} times, and the best one for you applies.",
            AbilityTargetKind.None,
            [BeforeRoll],
            new Dictionary<string, decimal> { [Draws] = 2 }),
        AbilityDefinition.Create(
            DoubleDice,
            "Double Dice",
            "Use before you roll. {dice} dice are rolled for you and you move by the one you keep.",
            AbilityTargetKind.None,
            [BeforeRoll],
            new Dictionary<string, decimal> { [Dice] = 2 }),
        AbilityDefinition.Create(
            TaxTheRich,
            "Tax the Rich",
            "Use before you roll. Take {percent}% of the liquid MON of the richest opponent, at most {cap} MON.",
            AbilityTargetKind.None,
            [BeforeRoll],
            new Dictionary<string, decimal> { [Percent] = 10, [Cap] = 50 }),
    ];

    private sealed class ArmHandler(ChogLifeMatchRules game, string abilityId, IReadOnlyList<AbilityParameter> parameters) : IAbilityHandler
    {
        public string AbilityId => abilityId;

        public IReadOnlyList<AbilityParameter> Parameters => parameters;

        public string? CanExecute(AbilityContext context) => game.CanArm(context);

        public JsonElement Execute(AbilityContext context) => game.Arm(context);
    }

    private sealed class DiceHandler(ChogLifeMatchRules game) : IAbilityHandler
    {
        public string AbilityId => DoubleDice;

        public IReadOnlyList<AbilityParameter> Parameters { get; } = [new AbilityParameter(Dice, 2, MaxDice)];

        public string? CanExecute(AbilityContext context) => game.CanRollDice(context);

        public JsonElement Execute(AbilityContext context) => game.RollDice(context);
    }

    private sealed class TaxHandler(ChogLifeMatchRules game) : IAbilityHandler
    {
        public string AbilityId => TaxTheRich;

        public IReadOnlyList<AbilityParameter> Parameters { get; } =
            [new AbilityParameter(Percent, 1, 100), new AbilityParameter(Cap, 1, MaxCap)];

        public string? CanExecute(AbilityContext context) => game.CanTax(context);

        public JsonElement Execute(AbilityContext context) => game.Tax(context);
    }

    private sealed class Timing(ChogLifeMatchRules game) : IAbilityTiming
    {
        public string? CurrentWindow(AbilityContext context) => game.AbilityWindow(context);
    }
}
