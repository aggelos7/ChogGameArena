using ChogGames.Core.Players;
using ChogGames.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ChogGames.Infrastructure.Moderation;

/// <summary>
/// Reports of uploaded photos and their review (STORY-069, DEC-070). Every change runs under the photo owner's row lock,
/// the same lock their uploads and removals take, so a report never lands on a photo being replaced, and two reports at
/// once count exactly once each.
/// </summary>
public sealed class PhotoReportService(ChogGamesDbContext db, TimeProvider timeProvider, IOptions<ModerationOptions> options)
{
    /// <summary>
    /// Records <paramref name="reporterId"/>'s report of the photo, hiding it once enough different players report it.
    /// </summary>
    public async Task<PhotoReportOutcome> ReportAsync(
        Guid reporterId, Guid imageId, AvatarImageReportReason reason, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (await LockOwnerAsync(imageId, cancellationToken) is not { } image)
        {
            return PhotoReportOutcome.NotFound;
        }

        if (image.PlayerId == reporterId)
        {
            return PhotoReportOutcome.OwnPhoto;
        }

        if (await db.AvatarImageReports.AnyAsync(r => r.ImageId == imageId && r.ReporterId == reporterId, cancellationToken))
        {
            return PhotoReportOutcome.AlreadyReported;
        }

        var now = timeProvider.GetUtcNow();
        db.AvatarImageReports.Add(AvatarImageReport.Create(imageId, reporterId, reason, now));
        await db.SaveChangesAsync(cancellationToken);
        db.ChangeTracker.Clear();

        var reporters = await db.AvatarImageReports.CountAsync(r => r.ImageId == imageId && r.DismissedAt == null, cancellationToken);
        if (image.HiddenAt is null && reporters >= options.Value.HideAfterReporters)
        {
            await db.AvatarImages.Where(i => i.Id == imageId)
                .ExecuteUpdateAsync(s => s.SetProperty(i => i.HiddenAt, now), cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return PhotoReportOutcome.Reported;
    }

    /// <summary>Every photo with reports still to review, the most reported first, then the oldest report first.</summary>
    public async Task<IReadOnlyList<ReportedPhoto>> ListAsync(CancellationToken cancellationToken)
    {
        var rows = await db.AvatarImageReports.AsNoTracking()
            .Where(r => r.DismissedAt == null)
            .Join(db.AvatarImages, r => r.ImageId, i => i.Id, (r, i) => new { r.ImageId, r.Reason, r.CreatedAt, i.PlayerId, i.HiddenAt })
            .Join(db.Players, x => x.PlayerId, p => p.Id, (x, p) => new { x.ImageId, x.Reason, x.CreatedAt, x.HiddenAt, p.DisplayName })
            .ToListAsync(cancellationToken);

        return [.. rows
            .GroupBy(r => r.ImageId)
            .Select(g => new ReportedPhoto(
                g.Key,
                g.First().DisplayName,
                g.Count(),
                g.GroupBy(r => r.Reason).ToDictionary(r => r.Key, r => r.Count()),
                g.Min(r => r.CreatedAt),
                g.Max(r => r.CreatedAt),
                g.First().HiddenAt))
            .OrderByDescending(p => p.Reporters)
            .ThenBy(p => p.FirstReportedAt)];
    }

    /// <summary>
    /// Deletes a reported photo, its reports with it, and the owner's built-in avatar shows again (DEC-018); false when
    /// there is no such photo (any longer).
    /// </summary>
    public async Task<bool> RemoveAsync(Guid imageId, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (await LockOwnerAsync(imageId, cancellationToken) is null)
        {
            return false;
        }

        await db.AvatarImages.Where(i => i.Id == imageId).ExecuteDeleteAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    /// <summary>
    /// Dismisses the photo's reports and shows it again; its reporters can't report it again. False when there is no
    /// such photo (any longer).
    /// </summary>
    public async Task<bool> DismissAsync(Guid imageId, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (await LockOwnerAsync(imageId, cancellationToken) is null)
        {
            return false;
        }

        var now = timeProvider.GetUtcNow();
        await db.AvatarImageReports.Where(r => r.ImageId == imageId && r.DismissedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.DismissedAt, now), cancellationToken);
        await db.AvatarImages.Where(i => i.Id == imageId)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.HiddenAt, (DateTimeOffset?)null), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    /// <summary>
    /// Locks the row of the player who owns the photo, then reads the photo again under that lock; null when there is no
    /// such photo, or it was replaced or removed meanwhile.
    /// </summary>
    private async Task<AvatarImageOwner?> LockOwnerAsync(Guid imageId, CancellationToken cancellationToken)
    {
        var ownerId = await db.AvatarImages.AsNoTracking().Where(i => i.Id == imageId).Select(i => (Guid?)i.PlayerId)
            .SingleOrDefaultAsync(cancellationToken);
        if (ownerId is null)
        {
            return null;
        }

        await db.Database.SqlQuery<Guid>($"""SELECT "Id" AS "Value" FROM "Players" WHERE "Id" = {ownerId} FOR UPDATE""")
            .ToListAsync(cancellationToken);

        return await db.AvatarImages.AsNoTracking().Where(i => i.Id == imageId && i.PlayerId == ownerId)
            .Select(i => new AvatarImageOwner(i.PlayerId, i.HiddenAt))
            .SingleOrDefaultAsync(cancellationToken);
    }

    private sealed record AvatarImageOwner(Guid PlayerId, DateTimeOffset? HiddenAt);
}

public enum PhotoReportOutcome
{
    Reported,
    NotFound,
    OwnPhoto,
    AlreadyReported,
}

/// <summary>A photo with reports still to review, as operators see it.</summary>
/// <param name="Reporters">Players whose reports still count; one report each.</param>
/// <param name="HiddenAt">When the reports hid it from other players; null while it shows.</param>
public sealed record ReportedPhoto(
    Guid ImageId,
    string OwnerName,
    int Reporters,
    IReadOnlyDictionary<AvatarImageReportReason, int> Reasons,
    DateTimeOffset FirstReportedAt,
    DateTimeOffset LastReportedAt,
    DateTimeOffset? HiddenAt);
