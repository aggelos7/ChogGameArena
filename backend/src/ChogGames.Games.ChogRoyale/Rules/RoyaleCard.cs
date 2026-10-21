namespace ChogGames.Games.ChogRoyale.Rules;

/// <summary>
/// The Chog Royale cards (STORY-073, ROYALE-015–020, DEC-084). Which phase of a round each one acts in, and what it does,
/// is the resolution's (STORY-074, DEC-085); their values are the rules version's.
/// </summary>
public enum RoyaleCard
{
    /// <summary>Gain MON.</summary>
    Pump,

    /// <summary>Steal MON from an opponent; targeted.</summary>
    RugPull,

    /// <summary>Block the first hostile effect aimed at the player this round and gain a little MON.</summary>
    DiamondHands,

    /// <summary>Exchange part of the balance with an opponent; targeted.</summary>
    WhaleSwap,

    /// <summary>Gain a server-random amount of MON.</summary>
    Airdrop,

    /// <summary>Add one random market modifier for everyone this round.</summary>
    Chaos,
}

public static class RoyaleCards
{
    /// <summary>Cards aimed at one opponent, who must be named (ROYALE-017).</summary>
    public static bool IsTargeted(this RoyaleCard card) => card is RoyaleCard.RugPull or RoyaleCard.WhaleSwap;
}
