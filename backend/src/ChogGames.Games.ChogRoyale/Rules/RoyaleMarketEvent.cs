namespace ChogGames.Games.ChogRoyale.Rules;

/// <summary>
/// The shared market events (STORY-077, ROYALE-021–023, DEC-088): one is drawn for every round and announced before its
/// decisions open. Chaos adds one more of the modifiers, never Calm Market (STORY-076, ROYALE-024, DEC-087). Their values
/// are the rules version's.
/// </summary>
public enum RoyaleMarketEvent
{
    /// <summary>No modifier.</summary>
    CalmMarket,

    /// <summary>Pump gains more.</summary>
    MemeHype,

    /// <summary>Pump gains less.</summary>
    MarketCrash,

    /// <summary>A successful Rug Pull steals a little more, within the victim's balance.</summary>
    WhaleIncoming,

    /// <summary>Everyone gains a little MON after the cards.</summary>
    CommunityAirdrop,
}
