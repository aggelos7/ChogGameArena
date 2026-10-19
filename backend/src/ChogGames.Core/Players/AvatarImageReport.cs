namespace ChogGames.Core.Players;

/// <summary>
/// One player's report of another player's uploaded photo (STORY-069, DEC-070): a fixed reason and no free text, so
/// reports need no moderation themselves. One per reporter and photo, dismissed reports included, so a dismissed
/// reporter can't report the same photo again. Deleted with the photo or the reporter.
/// </summary>
public sealed class AvatarImageReport
{
    public Guid ImageId { get; private set; }

    public Guid ReporterId { get; private set; }

    public AvatarImageReportReason Reason { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>When an operator dismissed the photo's reports; null while the report counts.</summary>
    public DateTimeOffset? DismissedAt { get; private set; }

    private AvatarImageReport()
    {
    }

    public static AvatarImageReport Create(Guid imageId, Guid reporterId, AvatarImageReportReason reason, DateTimeOffset now)
    {
        if (!Enum.IsDefined(reason))
        {
            throw new ArgumentOutOfRangeException(nameof(reason), reason, "Unknown report reason.");
        }

        return new AvatarImageReport { ImageId = imageId, ReporterId = reporterId, Reason = reason, CreatedAt = now };
    }
}

/// <summary>Why a photo was reported; the only thing a reporter says.</summary>
public enum AvatarImageReportReason
{
    Sexual,
    Hate,
    Violence,
    Scam,
    Other,
}
