namespace ChogGames.Games.ChogLife.Rules;

/// <summary>
/// Chog Life rules version 2 (STORY-044, DEC-047): version 1's board, events, stakes, die and cap, with a first-finish
/// bonus of 50 MON instead of 100. Seeded simulations showed the first player to finish winning 82% of two-player
/// matches with 100, against LIFE-007; with 50 a quick finish still pays without deciding the match. Matches started
/// under version 1 keep playing it.
/// </summary>
public static class LifeRulesV2
{
    public const int Version = 2;

    public const int FinishBonus = 50;

    /// <summary>Built when the class is first used, so a malformed board fails at startup (DEC-037).</summary>
    public static LifeRules Rules { get; } = LifeRules.Create(
        Version,
        LifeRulesV1.Rules.Tiles,
        LifeRulesV1.Rules.StartingMon,
        FinishBonus,
        LifeRulesV1.Rules.TimeCap,
        LifeRulesV1.Rules.DieFaces,
        LifeRulesV1.Rules.Events.Values,
        LifeRulesV1.Rules.DefaultLane,
        LifeRulesV1.Rules.ForkStake,
        LifeRulesV1.Rules.Stakes.Values);
}
