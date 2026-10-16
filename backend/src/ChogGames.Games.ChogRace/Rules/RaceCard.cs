namespace ChogGames.Games.ChogRace.Rules;

/// <summary>
/// The Chog Race cards (RACE-005, STORY-052, DEC-056). Cards of a kind are alike, so a deck or hand is a list of kinds.
/// They are drawn and held from STORY-052. The movement and chaos cards are played on their holder's turn from STORY-053
/// (DEC-057), with the values of <see cref="RaceCardValues"/>, and the attack cards from STORY-054 (DEC-058); HODL is
/// played only in reply to Rug, Liquidated or Whale aimed at its holder.
/// </summary>
public enum RaceCard
{
    /// <summary>Move forward <see cref="RaceCardValues.Pump"/> tiles (STORY-053).</summary>
    Pump,

    /// <summary>Send an opponent back <see cref="RaceCardValues.Rug"/> tiles (STORY-054).</summary>
    Rug,

    /// <summary>
    /// Block a Rug, Liquidated or Whale aimed at the holder, played in reply to it rather than on their turn (STORY-054,
    /// DEC-058).
    /// </summary>
    Hodl,

    /// <summary>Swap tiles with an opponent the player picks (STORY-053); HODL can block it (STORY-054).</summary>
    Whale,

    /// <summary>An additional movement action: the server rolls again (STORY-053).</summary>
    Airdrop,

    /// <summary>An opponent skips their next turn (STORY-054).</summary>
    Liquidated,

    /// <summary>Move forward or back, at random (STORY-053).</summary>
    Fomo,

    /// <summary>Every opponent moves back <see cref="RaceCardValues.Reverse"/> tiles (STORY-053).</summary>
    Reverse,
}
