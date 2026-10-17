using ChogGames.Core.Achievements;
using ChogGames.Core.Rooms;
using ChogGames.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ChogGames.Infrastructure.Achievements;

/// <summary>
/// Achievements from verified match results (STORY-058, DEC-063). Games played and wins are the profile's own stats;
/// games tried and the best Degen Score come from completed matches' stored members and results, which only the server
/// writes. A game's result takes part in Full Degen by carrying a numeric <c>degenScore</c>.
/// </summary>
public static class PlayerAchievements
{
    /// <summary>
    /// Each of <paramref name="playerIds"/>' totals over their completed matches; a player who doesn't exist is left out.
    /// </summary>
    public static async Task<IReadOnlyDictionary<Guid, AchievementCounts>> AchievementCountsAsync(
        this ChogGamesDbContext db, Guid[] playerIds, CancellationToken cancellationToken)
    {
        var rows = await db.Database.SqlQuery<CountsRow>(
            $"""
            SELECT p."Id" AS "PlayerId", p."GamesPlayed", p."Wins",
                (SELECT COUNT(DISTINCT r."GameType")::int
                    FROM "RoomMembers" m JOIN "GameRooms" r ON r."Id" = m."RoomId"
                    WHERE m."PlayerId" = p."Id" AND r."Status" = {nameof(RoomStatus.Completed)}) AS "GamesTried",
                COALESCE((SELECT MAX(LEAST(GREATEST((m."Result" ->> 'degenScore')::numeric, 0), 100))::int
                    FROM "RoomMembers" m JOIN "GameRooms" r ON r."Id" = m."RoomId"
                    WHERE m."PlayerId" = p."Id" AND r."Status" = {nameof(RoomStatus.Completed)}
                        AND jsonb_typeof(m."Result" -> 'degenScore') = 'number'), 0) AS "BestDegenScore"
            FROM "Players" p
            WHERE p."Id" = ANY({playerIds})
            """).ToListAsync(cancellationToken);

        return rows.ToDictionary(r => r.PlayerId, r => new AchievementCounts(r.GamesPlayed, r.Wins, r.GamesTried, r.BestDegenScore));
    }

    /// <summary>
    /// Unlocks what <paramref name="room"/>'s members have now earned, dated when the match ended. Called by
    /// <c>RecordStatsAsync</c> after the stats, in the transaction that completes the match and with its players locked;
    /// an achievement already unlocked is never unlocked again.
    /// </summary>
    internal static async Task AwardAchievementsAsync(this ChogGamesDbContext db, GameRoom room, Guid[] memberIds, CancellationToken cancellationToken)
    {
        var unlockedAt = room.EndedAt ?? throw new InvalidOperationException("Only an ended match unlocks achievements.");
        var counts = await db.AchievementCountsAsync(memberIds, cancellationToken);
        foreach (var (playerId, totals) in counts)
        {
            foreach (var achievement in Achievement.All.Where(a => a.IsEarnedBy(totals)))
            {
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                    INSERT INTO "PlayerAchievements" ("PlayerId", "AchievementId", "RoomId", "UnlockedAt")
                    VALUES ({playerId}, {achievement.Id}, {room.Id}, {unlockedAt})
                    ON CONFLICT ("PlayerId", "AchievementId") DO NOTHING
                    """,
                    cancellationToken);
            }
        }
    }

    private sealed class CountsRow
    {
        public Guid PlayerId { get; init; }

        public int GamesPlayed { get; init; }

        public int Wins { get; init; }

        public int GamesTried { get; init; }

        public int BestDegenScore { get; init; }
    }
}
