namespace ChogGames.Infrastructure.Matches;

/// <summary>Match turn settings (STORY-025, ENG-007, DEC-027), bound from the <c>Matches</c> configuration section.</summary>
public sealed class MatchOptions
{
    public const string SectionName = "Matches";

    public static readonly TimeSpan MinTurnTimeout = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan MaxTurnTimeout = TimeSpan.FromMinutes(10);

    /// <summary>How long a player has for each turn. A match keeps the value it started with.</summary>
    public TimeSpan TurnTimeout { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>A player whose turn runs out this many times in a row forfeits the match.</summary>
    public int MaxMissedTurns { get; set; } = 3;

    /// <summary>How often overdue turns are looked for, so a turn times out at most this long after it runs out.</summary>
    public TimeSpan TimeoutCheckInterval { get; set; } = TimeSpan.FromSeconds(1);
}
