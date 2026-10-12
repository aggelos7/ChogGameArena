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
}
