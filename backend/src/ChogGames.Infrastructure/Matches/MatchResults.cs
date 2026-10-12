using ChogGames.Core.Rooms;
using ChogGames.Infrastructure.Persistence;
using ChogGames.Infrastructure.Rooms;
using Microsoft.EntityFrameworkCore;

namespace ChogGames.Infrastructure.Matches;

/// <summary>
/// Player stats from match results (STORY-028, SUITE-004, DEC-030). Only a completed match counts: every member,
/// including those who left it, has played one more game, and each player ranked first has one more win. An abandoned
/// match changes no stats. Called in the transaction that completes the match under its row lock, so a match is counted
/// exactly once.
/// </summary>
internal static class MatchResults
{
    /// <summary>Adds <paramref name="room"/>'s results to its members' stats; the room has just been completed.</summary>
    public static async Task RecordStatsAsync(this ChogGamesDbContext db, GameRoom room, CancellationToken cancellationToken)
    {
        if (room.Status != RoomStatus.Completed)
        {
            throw new InvalidOperationException("Only a completed match changes player stats.");
        }

        var memberIds = room.Members.Select(m => m.PlayerId).ToArray();
        var winnerIds = room.Members.Where(m => m.Won).Select(m => m.PlayerId).ToArray();

        // The room is locked; its players next, in id order (RoomLocks). The increments are atomic in the database, so
        // a profile change committed meanwhile is neither lost nor overwritten.
        await db.LockPlayersAsync(memberIds, cancellationToken);
        await db.Players
            .Where(p => memberIds.Contains(p.Id))
            .ExecuteUpdateAsync(set => set.SetProperty(p => p.GamesPlayed, p => p.GamesPlayed + 1), cancellationToken);
        await db.Players
            .Where(p => winnerIds.Contains(p.Id))
            .ExecuteUpdateAsync(set => set.SetProperty(p => p.Wins, p => p.Wins + 1), cancellationToken);
    }
}
