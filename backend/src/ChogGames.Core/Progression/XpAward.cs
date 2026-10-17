namespace ChogGames.Core.Progression;

/// <summary>
/// The XP a player earned in one completed match (STORY-059, DEC-064), once per player and match. Written only by the
/// server when it records a match's results, never by a client; leaderboards add these up.
/// </summary>
public sealed class XpAward
{
    public Guid RoomId { get; private set; }

    public Guid PlayerId { get; private set; }

    /// <summary>The match's game, so per-game boards don't need the room.</summary>
    public string GameType { get; private set; } = string.Empty;

    /// <summary>When the match ended; seasons are counted by it.</summary>
    public DateTimeOffset EarnedAt { get; private set; }

    /// <summary>Members ranked below the player.</summary>
    public int OpponentsBeaten { get; private set; }

    /// <summary>Ranked first.</summary>
    public bool Won { get; private set; }

    public int Xp { get; private set; }

    /// <summary>The <see cref="XpRules.Version"/> the XP was worked out with.</summary>
    public int RulesVersion { get; private set; }

    private XpAward()
    {
    }
}
