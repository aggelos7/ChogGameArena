namespace ChogGames.Core.Achievements;

/// <summary>
/// An achievement a player unlocked (STORY-058, DEC-063), once per player and achievement, by the completed match in
/// <see cref="RoomId"/>. Written only by the server when it records a match's results, never by a client.
/// </summary>
public sealed class PlayerAchievement
{
    public Guid PlayerId { get; private set; }

    /// <summary>An <see cref="Achievement"/> id; one retired from the catalog is kept but not shown.</summary>
    public string AchievementId { get; private set; } = string.Empty;

    /// <summary>The completed match that unlocked it.</summary>
    public Guid RoomId { get; private set; }

    /// <summary>When that match ended.</summary>
    public DateTimeOffset UnlockedAt { get; private set; }

    private PlayerAchievement()
    {
    }
}
