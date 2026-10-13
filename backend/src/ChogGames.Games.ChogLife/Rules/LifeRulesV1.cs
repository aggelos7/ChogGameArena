using static ChogGames.Games.ChogLife.Rules.LifeLane;
using static ChogGames.Games.ChogLife.Rules.LifeEventOutcome;
using static ChogGames.Games.ChogLife.Rules.LifeTile;

namespace ChogGames.Games.ChogLife.Rules;

/// <summary>
/// Chog Life rules version 1 (STORY-035, DEC-037): 36 tiles on a main track with two Safe/Degen forks, 100 MON to
/// start, +100 MON for the first player to finish, a 20-minute cap, one six-sided die, the event outcomes of
/// STORY-038 (DEC-040) and the safe lane for a player who doesn't choose at a fork (STORY-039, DEC-041).
/// These are proposed defaults to be balanced in STORY-044 (requirements §15); any change means version 2.
/// </summary>
public static class LifeRulesV1
{
    public const int Version = 1;

    /// <summary>Built when the class is first used, so a malformed board fails at startup (DEC-037).</summary>
    public static LifeRules Rules { get; } = LifeRules.Create(
        Version,
        Board(),
        startingMon: 100,
        finishBonus: 100,
        timeCap: TimeSpan.FromMinutes(20),
        dieFaces: 6,
        events: Events(),
        defaultLane: LifeLane.Safe);

    /// <summary>
    /// Airdrops always pay, a market moves 10% of liquid MON either way, a rug takes 40% and a liquidation 25%, at least
    /// 20 MON. Losses never take more than the player has.
    /// </summary>
    private static IEnumerable<LifeEvent> Events() =>
    [
        new(LifeEventKind.Airdrop, [Gain(60, 10), Gain(30, 25), Gain(10, 50)]),
        new(LifeEventKind.Market, [GainPercent(50, 10, minimum: 5), LosePercent(50, 10, minimum: 5)]),
        new(LifeEventKind.Rug, [LosePercent(100, 40)]),
        new(LifeEventKind.Liquidation, [LosePercent(100, 25, minimum: 20)]),
    ];

    // S-1..8-[B9] safe 10-13 / degen 14-17 -> 18..24-[B25] safe 26-28 / degen 29-31 -> 32..34-F35
    private static IEnumerable<LifeTile> Board() =>
    [
        Start(0, 1),
        Normal(1, Main, 2),
        Reward(2, Main, 3, 20),
        EventTile(3, Main, 4, LifeEventKind.Airdrop),
        Normal(4, Main, 5),
        Loss(5, Main, 6, 15),
        EventTile(6, Main, 7, LifeEventKind.Market),
        Reward(7, Main, 8, 25),
        Normal(8, Main, 9),
        Branch(9, safeNext: 10, degenNext: 14),

        Reward(10, Safe, 11, 10),
        Normal(11, Safe, 12),
        Reward(12, Safe, 13, 15),
        Normal(13, Safe, 18),

        EventTile(14, Degen, 15, LifeEventKind.Rug),
        Reward(15, Degen, 16, 60),
        EventTile(16, Degen, 17, LifeEventKind.Liquidation),
        EventTile(17, Degen, 18, LifeEventKind.Airdrop),

        Normal(18, Main, 19),
        EventTile(19, Main, 20, LifeEventKind.Market),
        Loss(20, Main, 21, 20),
        Reward(21, Main, 22, 30),
        EventTile(22, Main, 23, LifeEventKind.Airdrop),
        Normal(23, Main, 24),
        EventTile(24, Main, 25, LifeEventKind.Rug),
        Branch(25, safeNext: 26, degenNext: 29),

        Reward(26, Safe, 27, 15),
        Normal(27, Safe, 28),
        Reward(28, Safe, 32, 20),

        EventTile(29, Degen, 30, LifeEventKind.Liquidation),
        Reward(30, Degen, 31, 80),
        EventTile(31, Degen, 32, LifeEventKind.Rug),

        EventTile(32, Main, 33, LifeEventKind.Market),
        Loss(33, Main, 34, 25),
        Reward(34, Main, 35, 30),
        Finish(35),
    ];
}
