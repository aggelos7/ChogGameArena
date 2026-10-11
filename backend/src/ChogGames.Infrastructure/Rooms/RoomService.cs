using ChogGames.Core.Games;
using ChogGames.Core.Players;
using ChogGames.Core.Rooms;
using ChogGames.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace ChogGames.Infrastructure.Rooms;

public enum RoomCreationStatus
{
    Created,

    /// <summary>The game is not in the catalogue or has no registered definition.</summary>
    UnknownGame,

    /// <summary>The game is listed but not enabled (coming soon or under maintenance).</summary>
    GameUnavailable,

    /// <summary>The player already hosts a waiting room (DEC-020).</summary>
    AlreadyHosting,
}

public sealed record RoomCreation(RoomCreationStatus Status, GameRoom? Room)
{
    public static RoomCreation Refused(RoomCreationStatus status) => new(status, null);
}

/// <summary>
/// Creates private rooms (STORY-019, ENG-001). The host is the authenticated player, never client input; the game
/// must be registered and enabled; the code is drawn by the server and is unique.
/// </summary>
public sealed class RoomService(
    ChogGamesDbContext db,
    IGameCatalog catalog,
    IRoomCodeGenerator codes,
    TimeProvider timeProvider,
    ILogger<RoomService> logger)
{
    /// <summary>Codes drawn before giving up; with ~887 million codes a second draw is already rare.</summary>
    public const int MaxCodeAttempts = 5;

    private const string CodeIndex = "IX_GameRooms_Code";
    private const string HostIndex = "IX_GameRooms_HostPlayerId";

    public async Task<RoomCreation> CreateAsync(Player host, string gameType, CancellationToken cancellationToken)
    {
        var listed = catalog.Find(gameType);
        var game = listed is null
            ? null
            : await db.GameDefinitions.AsNoTracking().SingleOrDefaultAsync(g => g.Id == gameType, cancellationToken);
        if (listed is null || game is null)
        {
            return RoomCreation.Refused(RoomCreationStatus.UnknownGame);
        }

        if (listed.Status != GameStatus.Available)
        {
            return RoomCreation.Refused(RoomCreationStatus.GameUnavailable);
        }

        if (await db.GameRooms.AnyAsync(r => r.HostPlayerId == host.Id && r.Status == RoomStatus.Waiting, cancellationToken))
        {
            return RoomCreation.Refused(RoomCreationStatus.AlreadyHosting);
        }

        for (var attempt = 1; attempt <= MaxCodeAttempts; attempt++)
        {
            var room = GameRoom.Create(game, host, codes.Next(), timeProvider.GetUtcNow());
            db.GameRooms.Add(room);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                return new RoomCreation(RoomCreationStatus.Created, room);
            }
            catch (DbUpdateException error) when (error.InnerException is PostgresException
                {
                    SqlState: PostgresErrorCodes.UniqueViolation,
                    ConstraintName: CodeIndex or HostIndex,
                } violation)
            {
                // Only this room's entries are detached; the caller's tracked player is left alone.
                Detach(room);
                if (violation.ConstraintName == HostIndex)
                {
                    // A concurrent request by the same host created a room first.
                    return RoomCreation.Refused(RoomCreationStatus.AlreadyHosting);
                }

                logger.LogInformation("Room code collision on attempt {Attempt}; drawing another code.", attempt);
            }
        }

        throw new InvalidOperationException($"No unused room code was found in {MaxCodeAttempts} attempts.");
    }

    private void Detach(GameRoom room)
    {
        var entries = db.ChangeTracker.Entries()
            .Where(entry => entry.Entity == room || entry.Entity is RoomMember member && member.RoomId == room.Id)
            .ToList();
        foreach (var entry in entries)
        {
            entry.State = EntityState.Detached;
        }
    }
}
