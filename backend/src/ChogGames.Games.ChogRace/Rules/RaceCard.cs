namespace ChogGames.Games.ChogRace.Rules;

/// <summary>
/// The Chog Race cards (RACE-005, STORY-052, DEC-056). Cards of a kind are alike, so a deck or hand is a list of kinds.
/// They are drawn and held from STORY-052. The movement and chaos cards are played on their holder's turn from STORY-053
/// (DEC-057), with the values of <see cref="RaceCardValues"/>; the attack and defense cards come with STORY-054.
/// </summary>
public enum RaceCard
{
    /// <summary>Move forward <see cref="RaceCardValues.Pump"/> tiles (STORY-053).</summary>
    Pump,

    /// <summary>Send an opponent back (STORY-054).</summary>
    Rug,

    /// <summary>Block an attack, played in reply to one rather than on the holder's turn (STORY-054, DEC-003).</summary>
    Hodl,

    /// <summary>Swap tiles with an opponent the player picks (STORY-053).</summary>
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
