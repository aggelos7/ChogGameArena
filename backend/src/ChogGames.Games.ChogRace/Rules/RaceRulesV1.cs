using static ChogGames.Games.ChogRace.Rules.RaceTile;

namespace ChogGames.Games.ChogRace.Rules;

/// <summary>
/// Chog Race rules version 1 (STORY-050, DEC-054): a 28-tile track with seven Card tiles, three Boosts and three
/// Setbacks, moves of 1 to 3 spaces and a 20-minute cap. These are proposed defaults to be balanced later
/// (requirements §15); any change means version 2.
/// </summary>
public static class RaceRulesV1
{
    public const int Version = 1;

    /// <summary>Built when the class is first used, so a malformed track fails at startup (DEC-054).</summary>
    public static RaceRules Rules { get; } = RaceRules.Create(
        Version,
        Track(),
        minMove: 1,
        maxMove: 3,
        timeCap: TimeSpan.FromMinutes(20));

    private static IEnumerable<RaceTile> Track() =>
    [
        Start(0),
        Normal(1),
        Card(2),
        Normal(3),
        Boost(4, to: 6),
        Normal(5),
        Normal(6),
        Card(7),
        Normal(8),
        Setback(9, to: 7),
        Normal(10),
        Card(11),
        Boost(12, to: 15),
        Normal(13),
        Card(14),
        Normal(15),
        Setback(16, to: 13),
        Normal(17),
        Card(18),
        Normal(19),
        Boost(20, to: 22),
        Normal(21),
        Card(22),
        Setback(23, to: 21),
        Normal(24),
        Card(25),
        Normal(26),
        Finish(27),
    ];
}
