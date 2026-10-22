namespace ChogGames.Games.ChogRoyale.Rules;

/// <summary>
/// Chog Royale rules version 2 (STORY-079, DEC-090): version 1 balanced after the simulations of
/// <c>docs/games/chog-royale-balancing.md</c>. Chaos also gains its player 10 MON and a successful Whale Swap 10, since
/// both moved no MON for their player on average and the players who played them won far less than a fair share. At the
/// product owner's request, players get longer to decide: 5 s announcement, 25 s decision, 10 s reveal and 5 s transition,
/// so 20 s between rounds and at most 3 min 30 s a match. Everything else is version 1's.
/// </summary>
public static class RoyaleRulesV2
{
    public const int Version = 2;

    /// <summary>Built when the class is first used, so invalid rules fail at startup (DEC-082).</summary>
    public static RoyaleRules Rules { get; } = Balanced(RoyaleRulesV1.Rules);

    private static RoyaleRules Balanced(RoyaleRules v1) => RoyaleRules.Create(
        Version,
        v1.Rounds,
        new RoyaleTiming(
            Announcement: TimeSpan.FromSeconds(5),
            Decision: TimeSpan.FromSeconds(25),
            Reveal: TimeSpan.FromSeconds(10),
            Transition: TimeSpan.FromSeconds(5)),
        v1.TimeCap,
        v1.StartingMon,
        v1.MaxMon,
        v1.Deck,
        v1.HandSize,
        v1.Effects with { ChaosGain = 10, SwapBonus = 10 },
        v1.Market);
}
