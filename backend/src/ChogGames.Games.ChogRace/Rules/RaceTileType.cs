namespace ChogGames.Games.ChogRace.Rules;

/// <summary>What landing on a Chog Race tile does (RACE-001, STORY-050, DEC-054). Effects resolve with STORY-051.</summary>
public enum RaceTileType
{
    /// <summary>Where every pawn begins; the board's first tile.</summary>
    Start,

    /// <summary>Nothing happens.</summary>
    Normal,

    /// <summary>The player draws a card, unless their hand is full (STORY-052, DEC-056).</summary>
    Card,

    /// <summary>The pawn jumps forward to the tile's <see cref="RaceTile.To"/>.</summary>
    Boost,

    /// <summary>The pawn falls back to the tile's <see cref="RaceTile.To"/>.</summary>
    Setback,

    /// <summary>The end of the race; the board's last tile. Reaching or passing it wins (RACE-008).</summary>
    Finish,
}
