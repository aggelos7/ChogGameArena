namespace ChogGames.Games.ChogLife.Rules;

/// <summary>What landing on a Chog Life tile does (LIFE-001, STORY-035, DEC-037).</summary>
public enum LifeTileType
{
    /// <summary>Where every player begins; the board's first tile.</summary>
    Start,

    /// <summary>Nothing happens.</summary>
    Normal,

    /// <summary>The player gains the tile's fixed amount of MON.</summary>
    Reward,

    /// <summary>The player loses the tile's fixed amount of MON.</summary>
    Loss,

    /// <summary>An event of the tile's <see cref="LifeEventKind"/>; its outcomes come with STORY-038.</summary>
    Event,

    /// <summary>A Safe/Degen fork: the player continues into the safe or the degen lane (LIFE-005).</summary>
    Branch,

    /// <summary>The end of the board; the board's last tile.</summary>
    Finish,
}
