using System.Text.Json;
using ChogGames.Core.Abilities;
using ChogGames.Core.Games;

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
/// </list>
/// The values here are the Base Tier's; holders of rarer Tiers lock stronger ones from <c>Abilities:Holder</c> (STORY-090,
/// DEC-077). Version 2 added <c>draws</c> and made <c>percent</c> of Loss Protection the share of a loss taken (unchanged at
/// 50%).
/// </summary>
public static class LifeAbilities
{
    public const int Version = 2;

    public const string RewardBoost = "reward_boost";
    public const string LossProtection = "loss_protection";
    public const string EventPreview = "event_preview";
    public const string EventReroll = "event_reroll";

    /// <summary>The player's turn, before they roll: not while choosing a lane, and not once they have finished.</summary>
    public const string BeforeRoll = "before_roll";

    /// <summary>The player's turn, while they choose a lane at a fork their roll stopped on.</summary>
    public const string AtFork = "at_fork";

    /// <summary>The share of a gain added, or of a loss still taken, in percent; a whole number of 1 to 100.</summary>
    public const string Percent = "percent";

    /// <summary>How many times Lucky Reroll draws an event, the best kept; 2 to <see cref="MaxDraws"/>.</summary>
    public const string Draws = "draws";

    public const int MaxDraws = 5;

    /// <summary>The catalog, version 2, whose handlers act on <paramref name="game"/>'s match states.</summary>
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
    ];

    private sealed class ArmHandler(ChogLifeMatchRules game, string abilityId, IReadOnlyList<AbilityParameter> parameters) : IAbilityHandler
    {
        public string AbilityId => abilityId;

        public IReadOnlyList<AbilityParameter> Parameters => parameters;

        public string? CanExecute(AbilityContext context) => game.CanArm(context);

        public JsonElement Execute(AbilityContext context) => game.Arm(context);
    }

    private sealed class Timing(ChogLifeMatchRules game) : IAbilityTiming
    {
        public string? CurrentWindow(AbilityContext context) => game.AbilityWindow(context);
    }
}
