using ChogGames.Core.Rooms;
using ChogGames.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ChogGames.Api.Tests.Rooms;

/// <summary>
/// Puts a room straight into a status for tests, with the start and end times the database requires for it (DEC-025):
/// started unless waiting or cancelled, ended in a final status. A started room gets a turn as the migration
/// <c>AddMatchTurns</c> gives one (DEC-027): seats in join order, a 20-second turn, and the host's turn while active.
/// </summary>
internal static class RoomStatusOverride
{
    public static async Task SetAsync(ChogGamesDbContext db, string code, RoomStatus status, DateTimeOffset now)
    {
        var started = status is not (RoomStatus.Waiting or RoomStatus.Cancelled);
        var active = status == RoomStatus.Active;
        DateTimeOffset? endedAt = RoomLifecycle.IsFinal(status) ? now : null;
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "GameRooms"
            SET "Status" = {status.ToString()},
                "StartedAt" = CASE WHEN {started} THEN COALESCE("StartedAt", {now}) ELSE NULL END,
                "EndedAt" = {endedAt},
                "TurnTimeout" = CASE WHEN {started} THEN COALESCE("TurnTimeout", interval '20 seconds') ELSE NULL END,
                "TurnNumber" = CASE WHEN {started} THEN GREATEST("TurnNumber", 1) ELSE 0 END,
                "CurrentPlayerId" = CASE WHEN {active} THEN COALESCE("CurrentPlayerId", "HostPlayerId") ELSE NULL END,
                "TurnEndsAt" = CASE WHEN {active} THEN COALESCE("TurnEndsAt", {now} + interval '20 seconds') ELSE NULL END
            WHERE "Code" = {code}
            """);
        if (started)
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE "RoomMembers" AS m SET "Seat" = seated."Seat"
                FROM (
                    SELECT rm."RoomId", rm."PlayerId",
                        row_number() OVER (
                            PARTITION BY rm."RoomId" ORDER BY rm."PlayerId" <> r."HostPlayerId", rm."JoinedAt", rm."PlayerId") AS "Seat"
                    FROM "RoomMembers" AS rm JOIN "GameRooms" AS r ON r."Id" = rm."RoomId"
                    WHERE r."Code" = {code}
                ) AS seated
                WHERE m."RoomId" = seated."RoomId" AND m."PlayerId" = seated."PlayerId" AND m."Seat" IS NULL
                """);
        }
    }
}
