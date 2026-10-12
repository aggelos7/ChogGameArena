using ChogGames.Core.Rooms;
using ChogGames.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ChogGames.Infrastructure.Rooms;

/// <summary>
/// Row locks for room writes. Inside a transaction the room is always locked first and then its players in id order,
/// so concurrent writes cannot deadlock.
/// </summary>
internal static class RoomLocks
{
    /// <summary>Locks the room with this code and loads it tracked, read after the lock; null if there is none.</summary>
    public static async Task<GameRoom?> LockRoomAsync(this ChogGamesDbContext db, RoomCode code, CancellationToken cancellationToken)
    {
        var roomIds = await db.Database
            .SqlQuery<Guid>($"""SELECT "Id" AS "Value" FROM "GameRooms" WHERE "Code" = {code.Value} FOR UPDATE""")
            .ToListAsync(cancellationToken);
        return roomIds is [var roomId] ? await db.GameRooms.SingleAsync(r => r.Id == roomId, cancellationToken) : null;
    }

    /// <summary>Locks players in id order, so two transactions locking overlapping sets cannot deadlock.</summary>
    public static Task LockPlayersAsync(this ChogGamesDbContext db, Guid[] playerIds, CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlInterpolatedAsync(
            $"""SELECT 1 FROM (SELECT "Id" FROM "Players" WHERE "Id" = ANY({playerIds}) ORDER BY "Id" FOR UPDATE) AS locked""",
            cancellationToken);
}
