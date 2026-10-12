using ChogGames.Core.Rooms;
using ChogGames.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ChogGames.Api.Tests.Rooms;

/// <summary>
/// Puts a room straight into a status for tests, with the start and end times the database requires for it (DEC-025):
/// started unless waiting or cancelled, ended in a final status.
/// </summary>
internal static class RoomStatusOverride
{
    public static Task SetAsync(ChogGamesDbContext db, string code, RoomStatus status, DateTimeOffset now)
    {
        var started = status is not (RoomStatus.Waiting or RoomStatus.Cancelled);
        DateTimeOffset? endedAt = RoomLifecycle.IsFinal(status) ? now : null;
        return db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "GameRooms"
            SET "Status" = {status.ToString()},
                "StartedAt" = CASE WHEN {started} THEN COALESCE("StartedAt", {now}) ELSE NULL END,
                "EndedAt" = {endedAt}
            WHERE "Code" = {code}
            """);
    }
}
