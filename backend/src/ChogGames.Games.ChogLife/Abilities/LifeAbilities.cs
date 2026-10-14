using System.Text.Json;
using ChogGames.Core.Abilities;
using ChogGames.Core.Games;

namespace ChogGames.Games.ChogLife.Abilities;

/// <summary>
/// Chog Life's abilities (LIFE-008, STORY-042, DEC-044), one of which each player locks for a match and uses once.
/// <list type="bullet">
/// <item>Reward Boost, Loss Protection and Lucky Reroll are used on the player's turn before they roll. Each stays armed
/// until the first tile outcome it fits, which it changes, then it is spent: a gain from a Reward tile or an event earns
/// 25% more as bonuses, a loss from a Loss tile or an event is halved, and an event with several outcomes is drawn twice
/// and the better one for the player applies. Fork stakes and the finish bonus are never changed.</item>
/// <item>Event Preview is used at a fork before choosing a lane. Choosing then draws the next move on that lane and shows
/// only that player the die, the tile and the event outcome there (never a stake's payout). In the same turn they keep
/// the lane, and the roll plays out exactly as shown, or switch to the other lane blind, and the next roll is drawn
/// afresh. A turn that runs out keeps the lane.</item>
/// </list>
/// These are proposed defaults to balance in STORY-044; a change to any definition means catalog version 2.
/// </summary>
public static class LifeAbilities
{
    public const int Version = 1;

    public const string RewardBoost = "reward_boost";
    public const string LossProtection = "loss_protection";
    public const string EventPreview = "event_preview";
    public const string EventReroll = "event_reroll";

    /// <summary>The player's turn, before they roll: not while choosing a lane, and not once they have finished.</summary>
    public const string BeforeRoll = "before_roll";

    /// <summary>The player's turn, while they choose a lane at a fork their roll stopped on.</summary>
    public const string AtFork = "at_fork";

    /// <summary>The share of a gain added, or of a loss kept, in percent; a whole number of 1 to 100.</summary>
    public const string Percent = "percent";

    /// <summary>The catalog, version 1, whose handlers act on <paramref name="game"/>'s match states.</summary>
    public static AbilityCatalog Catalog(ChogLifeMatchRules game)
    {
        ArgumentNullException.ThrowIfNull(game);
        return AbilityCatalog.Create(
            GameDefinition.ChogLife,
            Version,
            Definitions(),
            [
                new ArmHandler(game, RewardBoost, withPercent: true),
                new ArmHandler(game, LossProtection, withPercent: true),
                new ArmHandler(game, EventReroll, withPercent: false),
                new ArmHandler(game, EventPreview, withPercent: false),
            ],
            new Timing(game));
    }

    private static IEnumerable<AbilityDefinition> Definitions() =>
    [
        AbilityDefinition.Create(
            RewardBoost,
            "Reward Boost",
            "Use before you roll. Your next gain from a reward tile or an event earns 25% more, added to your bonuses.",
            AbilityTargetKind.None,
            [BeforeRoll],
            new Dictionary<string, decimal> { [Percent] = 25 }),
        AbilityDefinition.Create(
            LossProtection,
            "Loss Protection",
            "Use before you roll. Your next loss from a loss tile or an event is halved.",
            AbilityTargetKind.None,
            [BeforeRoll],
            new Dictionary<string, decimal> { [Percent] = 50 }),
        AbilityDefinition.Create(
            EventPreview,
            "Event Preview",
            "Use at a fork, then choose a route: only you see what your next roll does there. Keep it, or switch to the other route blind.",
            AbilityTargetKind.None,
            [AtFork]),
        AbilityDefinition.Create(
            EventReroll,
            "Lucky Reroll",
            "Use before you roll. Your next event with several outcomes is drawn twice, and the better one for you applies.",
            AbilityTargetKind.None,
            [BeforeRoll]),
    ];

    private sealed class ArmHandler(ChogLifeMatchRules game, string abilityId, bool withPercent) : IAbilityHandler
    {
        public string AbilityId => abilityId;

        public IReadOnlyList<AbilityParameter> Parameters { get; } =
            withPercent ? [new AbilityParameter(Percent, 1, 100)] : [];

        public string? CanExecute(AbilityContext context) => game.CanArm(context);

        public JsonElement Execute(AbilityContext context) => game.Arm(context);
    }

    private sealed class Timing(ChogLifeMatchRules game) : IAbilityTiming
    {
        public string? CurrentWindow(AbilityContext context) => game.AbilityWindow(context);
    }
}
