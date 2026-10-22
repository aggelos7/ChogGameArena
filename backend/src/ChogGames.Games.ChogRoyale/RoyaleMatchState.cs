using ChogGames.Games.ChogRoyale.Rules;

namespace ChogGames.Games.ChogRoyale;

/// <summary>
/// A Chog Royale match's stored state (STORY-071–077, STORY-080, DEC-082–088, DEC-091): the rules version it started
/// with, the round being played and its market event, the next card instance number and every seated player, whose hand,
/// pending action and ability are private, plus the public ledger of every resolved round.
/// </summary>
/// <param name="Round">The round being played, from 1, which the engine's round counter matches.</param>
/// <param name="Event">The round's market event, drawn before its decisions open and never changed after (ROYALE-021).</param>
/// <param name="NextCard">The number the next dealt card gets, so card ids are unique in the match (ROYALE-019).</param>
/// <param name="NextEvent">
/// The next round's event, when Alpha Leak drew it early (DEC-091); the round's resolution then uses it instead of a draw.
/// </param>
public sealed record RoyaleMatchState(
    int RulesVersion,
    int Round,
    RoyaleMarketEvent Event,
    int NextCard,
    RoyalePlayer[] Players,
    RoyaleRoundLedger[] Ledger,
    RoyaleMarketEvent? NextEvent = null);

/// <param name="Action">What the player submitted this round, until it resolves; null while they have submitted nothing.</param>
/// <param name="Ability">The ability the player used this round, until it resolves (STORY-080, DEC-091).</param>
public sealed record RoyalePlayer(int Seat, int Mon, RoyaleHandCard[] Hand, RoyaleAction? Action = null, RoyaleAbilityUse? Ability = null);

/// <summary>
/// An ability used in the round (STORY-080, DEC-091): its id and the value it acts with, locked at the start: Diamond
/// Hands+'s extra blocks, Double Pump's percent, Stop Loss's most MON lost; 0 for the others.
/// </summary>
/// <param name="Target">The seat Insider Info looked at (STORY-093, DEC-095); null for the other abilities.</param>
public sealed record RoyaleAbilityUse(string Id, int Value, int? Target = null);

/// <summary>One card in a hand: its match-unique instance id (<c>c1</c>, <c>c2</c>, …) and which card it is.</summary>
public sealed record RoyaleHandCard(string Id, RoyaleCard Card);

/// <summary>
/// A round's action (STORY-072, DEC-083): HODL, which plays nothing and keeps the hand, or a card from the hand by its
/// instance id, with the target seat for a targeted card.
/// </summary>
public sealed record RoyaleAction(bool Hodl = false, string? Card = null, int? Target = null);

/// <summary>
/// One resolved round (STORY-074, ROYALE-013, DEC-085): its market event and the modifier Chaos added, if any (STORY-076,
/// STORY-077), the seat priority it resolved in and an entry per player still playing, in seat order.
/// </summary>
public sealed record RoyaleRoundLedger(
    int Round, RoyaleMarketEvent Event, RoyaleMarketEvent? Chaos, int[] Priority, RoyaleLedgerEntry[] Entries);

/// <param name="Card">The card played; null for HODL.</param>
/// <param name="Target">The seat a targeted card was aimed at.</param>
/// <param name="ByDefault">The player submitted nothing, so the round's default, HODL, was played for them (ROYALE-008).</param>
/// <param name="Outcome">What became of the card; null for HODL (DEC-086).</param>
/// <param name="Amount">
/// The MON the card moved for its player: Pump's, Airdrop's and Diamond Hands' gain, what a Rug Pull stole, and a Whale
/// Swap's net change, which may be negative.
/// </param>
/// <param name="Defense">What the player's Diamond Hands, or Diamond Hands+, did this round, if they had one up.</param>
/// <param name="Lost">The MON other players' cards took from the player this round (DEC-089).</param>
/// <param name="Market">The MON the round's market effects moved for the player.</param>
/// <param name="Change">The player's MON change over the round, after the clamp.</param>
/// <param name="Balance">The player's MON after the round.</param>
/// <param name="Ability">The ability the player used this round, shown from its reveal on (STORY-080, DEC-091).</param>
/// <param name="AbilityTarget">The seat whose hand the player's Insider Info showed them (STORY-093, DEC-095).</param>
public sealed record RoyaleLedgerEntry(
    int Seat,
    RoyaleCard? Card,
    int? Target,
    bool ByDefault,
    RoyaleOutcome? Outcome,
    int Amount,
    RoyaleDefense? Defense,
    int Lost,
    int Market,
    int Change,
    int Balance,
    string? Ability = null,
    int? AbilityTarget = null);

/// <summary>What became of a played card (DEC-086, DEC-087). Only <see cref="Applied"/> counts as a successful card.</summary>
public enum RoyaleOutcome
{
    /// <summary>The card had its effect.</summary>
    Applied,

    /// <summary>The target's Diamond Hands blocked it.</summary>
    Blocked,

    /// <summary>The target left before the round resolved.</summary>
    TargetLeft,

    /// <summary>A Whale Swap whose player or target was already in a swap, or a Chaos after the round's modifier was drawn.</summary>
    Taken,

    /// <summary>A Rug Pull on a player with nothing left to take.</summary>
    Empty,

    /// <summary>A Rug Pull the target's Stop Loss let nothing through (DEC-091).</summary>
    Stopped,
}

/// <summary>What a player's Diamond Hands, or Diamond Hands+, did (DEC-086, DEC-091).</summary>
public enum RoyaleDefense
{
    /// <summary>Raised, and nothing hostile came at the player.</summary>
    Raised,

    /// <summary>It blocked hostile cards aimed at the player, the first ones in resolution order.</summary>
    Blocked,
}
