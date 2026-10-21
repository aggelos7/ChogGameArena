namespace ChogGames.Games.ChogRoyale.Rules;

/// <summary>
/// Chog Royale rules version 1 (STORY-071, DEC-082): five rounds of 5 s announcement, 15 s decision, 10 s reveal and
/// 5 s transition (ROYALE-004), 100 MON each, balances of 0 to 10,000 and a 5-minute cap (ROYALE-003); hands of three
/// from a deck weighted Pump 30, Rug Pull 20, Diamond Hands 15, Airdrop 15, Whale Swap 10, Chaos 10 (STORY-073,
/// DEC-084). These are initial defaults to be balanced later (ROYALE-063, STORY-079); any change means version 2.
/// </summary>
public static class RoyaleRulesV1
{
    public const int Version = 1;

    /// <summary>Built when the class is first used, so invalid rules fail at startup (DEC-082).</summary>
    public static RoyaleRules Rules { get; } = RoyaleRules.Create(
        Version,
        rounds: 5,
        new RoyaleTiming(
            Announcement: TimeSpan.FromSeconds(5),
            Decision: TimeSpan.FromSeconds(15),
            Reveal: TimeSpan.FromSeconds(10),
            Transition: TimeSpan.FromSeconds(5)),
        timeCap: TimeSpan.FromMinutes(5),
        startingMon: 100,
        maxMon: 10_000,
        new Dictionary<RoyaleCard, int>
        {
            [RoyaleCard.Pump] = 30,
            [RoyaleCard.RugPull] = 20,
            [RoyaleCard.DiamondHands] = 15,
            [RoyaleCard.WhaleSwap] = 10,
            [RoyaleCard.Airdrop] = 15,
            [RoyaleCard.Chaos] = 10,
        },
        handSize: 3);
}
