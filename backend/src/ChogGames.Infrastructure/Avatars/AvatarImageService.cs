using ChogGames.Core.Players;
using ChogGames.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ChogGames.Infrastructure.Avatars;

/// <summary>
/// Stores, replaces and removes a player's one uploaded avatar (STORY-062, DEC-018). Each change runs under the player's
/// row lock, so two uploads at once leave exactly one image, the one committed last. Replacing or removing deletes the
/// old bytes; nothing is kept hidden.
/// </summary>
public sealed class AvatarImageService(ChogGamesDbContext db, TimeProvider timeProvider)
{
    /// <summary>Makes <paramref name="webp"/>, already re-encoded by the server, the player's avatar; false for an unknown player.</summary>
    public async Task<bool> ReplaceAsync(Guid playerId, byte[] webp, CancellationToken cancellationToken)
    {
        var image = AvatarImage.Create(playerId, webp, timeProvider.GetUtcNow());

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (!await LockPlayerAsync(playerId, cancellationToken))
        {
            return false;
        }

        await db.AvatarImages.Where(i => i.PlayerId == playerId).ExecuteDeleteAsync(cancellationToken);
        db.AvatarImages.Add(image);
        await db.SaveChangesAsync(cancellationToken);
        db.Entry(image).State = EntityState.Detached;
        await db.Players.Where(p => p.Id == playerId)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.AvatarImageId, image.Id), cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    /// <summary>
    /// Deletes the player's uploaded avatar, if any, so the built-in one shows again; false for an unknown player. The
    /// database clears <see cref="Player.AvatarImageId"/> with the image.
    /// </summary>
    public async Task<bool> RemoveAsync(Guid playerId, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (!await LockPlayerAsync(playerId, cancellationToken))
        {
            return false;
        }

        await db.AvatarImages.Where(i => i.PlayerId == playerId).ExecuteDeleteAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    /// <summary>The stored WebP with this id and who owns it, or null when there is none (any longer).</summary>
    public Task<StoredAvatarImage?> FindAsync(Guid imageId, CancellationToken cancellationToken) =>
        db.AvatarImages.AsNoTracking().Where(i => i.Id == imageId)
            .Select(i => new StoredAvatarImage(i.Data, i.PlayerId, i.HiddenAt != null))
            .SingleOrDefaultAsync(cancellationToken);

    /// <summary>Whether reports hid the player's current photo (STORY-069, DEC-070); false without one.</summary>
    public Task<bool> IsHiddenAsync(Guid playerId, CancellationToken cancellationToken) =>
        db.AvatarImages.AsNoTracking().AnyAsync(i => i.PlayerId == playerId && i.HiddenAt != null, cancellationToken);

    /// <summary>Locks the player's row; false when there is no such player. The only row these changes lock.</summary>
    private async Task<bool> LockPlayerAsync(Guid playerId, CancellationToken cancellationToken)
    {
        var locked = await db.Database.SqlQuery<Guid>($"""SELECT "Id" AS "Value" FROM "Players" WHERE "Id" = {playerId} FOR UPDATE""")
            .ToListAsync(cancellationToken);
        return locked.Count == 1;
    }
}

/// <summary>A stored photo as served: its WebP, its owner and whether reports hid it from other players.</summary>
public sealed record StoredAvatarImage(byte[] Webp, Guid PlayerId, bool Hidden);
