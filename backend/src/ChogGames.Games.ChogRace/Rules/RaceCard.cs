namespace ChogGames.Games.ChogRace.Rules;

/// <summary>
/// The Chog Race cards (RACE-005, STORY-052, DEC-056). Cards of a kind are alike, so a deck or hand is a list of kinds.
/// They are drawn and held from STORY-052; what each one does comes with STORY-053 (movement and chaos) and STORY-054
/// (attack and defense), so until then none can be played.
/// </summary>
public enum RaceCard
{
    /// <summary>Move forward (STORY-053).</summary>
    Pump,

    /// <summary>Send an opponent back (STORY-054).</summary>
    Rug,

    /// <summary>Block an attack, played in reply to one rather than on the holder's turn (STORY-054, DEC-003).</summary>
    Hodl,

    /// <summary>Swap positions (STORY-053).</summary>
    Whale,

    /// <summary>An additional movement action (STORY-053).</summary>
    Airdrop,

    /// <summary>An opponent skips their next turn (STORY-054).</summary>
    Liquidated,

    /// <summary>Move forward or back, at random (STORY-053).</summary>
    Fomo,

    /// <summary>Every opponent moves back (STORY-053).</summary>
    Reverse,
}
