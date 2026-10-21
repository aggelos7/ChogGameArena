using ChogGames.Games.ChogRoyale.Rules;

namespace ChogGames.Games.ChogRoyale;

/// <summary>
/// A Chog Royale match's stored state (STORY-071–074, DEC-082–085): the rules version it started with, the round being
/// played, the next card instance number and every seated player, whose hand and pending action are private, plus the
/// public ledger of every resolved round.
/// </summary>
/// <param name="Round">The round being played, from 1, which the engine's round counter matches.</param>
/// <param name="NextCard">The number the next dealt card gets, so card ids are unique in the match (ROYALE-019).</param>
public sealed record RoyaleMatchState(int RulesVersion, int Round, int NextCard, RoyalePlayer[] Players, RoyaleRoundLedger[] Ledger);

/// <param name="Action">What the player submitted this round, until it resolves; null while they have submitted nothing.</param>
public sealed record RoyalePlayer(int Seat, int Mon, RoyaleHandCard[] Hand, RoyaleAction? Action = null);

/// <summary>One card in a hand: its match-unique instance id (<c>c1</c>, <c>c2</c>, …) and which card it is.</summary>
public sealed record RoyaleHandCard(string Id, RoyaleCard Card);

/// <summary>
/// A round's action (STORY-072, DEC-083): HODL, which plays nothing and keeps the hand, or a card from the hand by its
/// instance id, with the target seat for a targeted card.
/// </summary>
public sealed record RoyaleAction(bool Hodl = false, string? Card = null, int? Target = null);

/// <summary>
/// One resolved round (STORY-074, ROYALE-013, DEC-085): the seat priority it resolved in and an entry per player still
/// playing, in seat order.
/// </summary>
public sealed record RoyaleRoundLedger(int Round, int[] Priority, RoyaleLedgerEntry[] Entries);

/// <param name="Card">The card played; null for HODL.</param>
/// <param name="Target">The seat a targeted card was aimed at.</param>
/// <param name="ByDefault">The player submitted nothing, so the round's default, HODL, was played for them (ROYALE-008).</param>
/// <param name="Defense">What the player's defense did this round, if anything; set by card effects (STORY-075).</param>
/// <param name="Market">The MON the round's market effects moved for the player.</param>
/// <param name="Change">The player's MON change over the round, after the clamp.</param>
/// <param name="Balance">The player's MON after the round.</param>
public sealed record RoyaleLedgerEntry(
    int Seat,
    RoyaleCard? Card,
    int? Target,
    bool ByDefault,
    string? Defense,
    int Market,
    int Change,
    int Balance);
