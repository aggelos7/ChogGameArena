namespace ChogGames.Games.ChogRoyale.Rules;

/// <summary>
/// Chog Royale rules version 1 (STORY-071, DEC-082): five rounds of 5 s announcement, 15 s decision, 10 s reveal and
/// 5 s transition (ROYALE-004), 100 MON each, balances of 0 to 10,000 and a 5-minute cap (ROYALE-003); hands of three
/// from a deck weighted Pump 30, Rug Pull 20, Diamond Hands 15, Airdrop 15, Whale Swap 10, Chaos 10 (STORY-073,
/// DEC-084); Pump 20, Rug Pull up to 15, Diamond Hands 5 (STORY-075, DEC-086); Whale Swap 20% up to 20 each way, Airdrop
/// 5–25 (STORY-076, DEC-087); market events weighted Calm Market 30, Meme Hype 20 (Pump 150%), Market Crash 20 (Pump 50%),
/// Whale Incoming 15 (Rug Pull +5), Community Airdrop 15 (+5 each), and Chaos drawing any modifier but the round's own with
/// equal weights (STORY-077, DEC-087, DEC-088). These are initial defaults to be balanced later (ROYALE-063, STORY-079); any change means version 2.
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
        handSize: 3,
        new RoyaleCardEffects(Pump: 20, RugPull: 15, DiamondHands: 5, SwapPercent: 20, SwapCap: 20, AirdropMin: 5, AirdropMax: 25),
        new RoyaleMarket(
            Events: new Dictionary<RoyaleMarketEvent, int>
            {
                [RoyaleMarketEvent.CalmMarket] = 30,
                [RoyaleMarketEvent.MemeHype] = 20,
                [RoyaleMarketEvent.MarketCrash] = 20,
                [RoyaleMarketEvent.WhaleIncoming] = 15,
                [RoyaleMarketEvent.CommunityAirdrop] = 15,
            },
            Chaos: new Dictionary<RoyaleMarketEvent, int>
            {
                [RoyaleMarketEvent.MemeHype] = 1,
                [RoyaleMarketEvent.MarketCrash] = 1,
                [RoyaleMarketEvent.WhaleIncoming] = 1,
                [RoyaleMarketEvent.CommunityAirdrop] = 1,
            },
            HypePercent: 150,
            CrashPercent: 50,
            WhaleBonus: 5,
            CommunityAirdrop: 5));
}
