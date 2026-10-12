using ChogGames.Core.Rooms;
using ChogGames.Infrastructure.Persistence;
using ChogGames.Infrastructure.Rooms;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ChogGames.Infrastructure.Matches;

/// <summary>
/// Restores active matches when the server starts (STORY-028, ENG-014, DEC-030). Every match change was committed with
/// its room, so the state, version, turn and players are as they were. The one thing downtime took is turn time: each
/// active match's current player gets a full new turn from now, with no missed turn, and the version is bumped so
/// commands built before the restart are stale. Each match is resumed in its own transaction under its row lock.
/// </summary>
public sealed class MatchRecoveryService(ChogGamesDbContext db, TimeProvider timeProvider, ILogger<MatchRecoveryService> logger)
{
    /// <summary>Resumes every active match and returns the codes of those resumed.</summary>
    public async Task<IReadOnlyList<string>> ResumeActiveMatchesAsync(CancellationToken cancellationToken)
    {
        var codes = await db.GameRooms.AsNoTracking()
            .Where(r => r.Status == RoomStatus.Active)
            .OrderBy(r => r.TurnEndsAt)
            .Select(r => r.Code)
            .ToListAsync(cancellationToken);

        var resumed = new List<string>();
        foreach (var code in codes)
        {
            try
            {
                if (await ResumeAsync(code, cancellationToken))
                {
                    resumed.Add(code);
                }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                // That match keeps its stored deadline and is timed out as usual (DEC-027); the others still resume.
                logger.LogError(error, "Match {Code} could not be resumed after the restart.", code);
            }
            finally
            {
                db.ChangeTracker.Clear();
            }
        }

        if (resumed.Count > 0)
        {
            logger.LogInformation("Resumed {Count} active matches after the restart.", resumed.Count);
        }

        return resumed;
    }

    private async Task<bool> ResumeAsync(string code, CancellationToken cancellationToken)
    {
        if (!RoomCode.TryParse(code, out var roomCode))
        {
            return false;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var room = await db.LockRoomAsync(roomCode, cancellationToken);

        // It may have ended since it was listed.
        if (room is null || !room.ResumeAfterRestart(timeProvider.GetUtcNow()))
        {
            return false;
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }
}
