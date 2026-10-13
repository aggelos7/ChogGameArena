namespace ChogGames.Games.ChogLife.Rules;

/// <summary>Which route a Chog Life tile lies on (LIFE-005, DEC-037).</summary>
public enum LifeLane
{
    /// <summary>The main track every player walks.</summary>
    Main,

    /// <summary>The lower-variance route after a <see cref="LifeTileType.Branch"/>.</summary>
    Safe,

    /// <summary>The high-risk route after a <see cref="LifeTileType.Branch"/>.</summary>
    Degen,
}
