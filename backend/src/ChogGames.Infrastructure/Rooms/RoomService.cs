using ChogGames.Core.Games;
using ChogGames.Core.Players;
using ChogGames.Core.Rooms;
using ChogGames.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace ChogGames.Infrastructure.Rooms;

public enum RoomOutcome
{
    Created,

    Joined,

    /// <summary>The player was already a member; nothing changed (DEC-021).</summary>
    AlreadyMember,

    /// <summary>The game is not in the catalogue or has no registered definition.</summary>
    UnknownGame,

    /// <summary>The game is listed but not enabled (coming soon or under maintenance).</summary>
    GameUnavailable,

    /// <summary>The player is already in another waiting room, as host or member, or still playing a started match (DEC-020, DEC-021, DEC-025).</summary>
    AlreadyInRoom,

    RoomNotFound,

    RoomFull,

    /// <summary>The room's match has started.</summary>
    RoomStarted,

    /// <summary>The room's match is over (completed or abandoned) or the room was cancelled.</summary>
    RoomClosed,

    /// <summary>The member's ready status was set (or already had that value).</summary>
    ReadyChanged,

    Started,

    /// <summary>Only the host can start the room.</summary>
    NotHost,

    /// <summary>Fewer members than the game's minimum.</summary>
    NotEnoughPlayers,

    /// <summary>A member is not ready, or changed Chog after marking ready.</summary>
    PlayersNotReady,

    /// <summary>A member's selected NFT is not owned by their wallet now; those members were set back to not ready.</summary>
    CharacterUnavailable,

    /// <summary>NFT ownership can't be read right now, so the room can't start (DEC-019); nothing changed.</summary>
    OwnershipUnavailable,

    /// <summary>
    /// The member left the room; when the host left a waiting room, it was cancelled (DEC-023). Leaving a started
    /// match is a forfeit, and the match is abandoned when fewer than two players remain (DEC-025).
    /// </summary>
    Left,

    /// <summary>The member had already left this started match; nothing changed.</summary>
    AlreadyLeft,
}

/// <param name="Room">The room when the outcome is <c>Created</c>, <c>Joined</c>, <c>AlreadyMember</c>, <c>ReadyChanged</c>, <c>Started</c>, <c>Left</c> or <c>AlreadyLeft</c>.</param>
/// <param name="OpenRoomCode">For <c>AlreadyInRoom</c>: the code of the room the player is already in.</param>
public sealed record RoomResult(RoomOutcome Outcome, GameRoom? Room = null, string? OpenRoomCode = null)
{
    public bool Succeeded => Room is not null;
}

/// <summary>
/// Creates and joins private rooms (STORY-019/020, ENG-001). The player is always the authenticated one, never
/// client input. Each operation locks the player's row (and, when joining, the room's row first), so concurrent
/// requests can neither put a player in two open rooms nor fill a room beyond its capacity.
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

    public async Task<RoomResult> CreateAsync(Player host, string gameType, CancellationToken cancellationToken)
    {
        var listed = catalog.Find(gameType);
        var game = listed is null
            ? null
            : await db.GameDefinitions.AsNoTracking().SingleOrDefaultAsync(g => g.Id == gameType, cancellationToken);
        if (listed is null || game is null)
        {
            return new RoomResult(RoomOutcome.UnknownGame);
        }

        if (listed.Status != GameStatus.Available)
        {
            return new RoomResult(RoomOutcome.GameUnavailable);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await LockPlayerAsync(host, cancellationToken);
        if (await OpenRoomCodeAsync(host, cancellationToken) is { } openRoom)
        {
            return new RoomResult(RoomOutcome.AlreadyInRoom, OpenRoomCode: openRoom);
        }

        for (var attempt = 1; attempt <= MaxCodeAttempts; attempt++)
        {
            var room = GameRoom.Create(game, host, codes.Next(), timeProvider.GetUtcNow());
            db.GameRooms.Add(room);
            try
            {
                // Inside a transaction EF saves behind a savepoint, so a failed insert can be retried.
                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return new RoomResult(RoomOutcome.Created, room);
            }
            catch (DbUpdateException error) when (error.InnerException is PostgresException
                {
                    SqlState: PostgresErrorCodes.UniqueViolation,
                    ConstraintName: CodeIndex,
                })
            {
                // Only this room's entries are detached; the caller's tracked player is left alone.
                Detach(room);
                logger.LogInformation("Room code collision on attempt {Attempt}; drawing another code.", attempt);
            }
        }

        throw new InvalidOperationException($"No unused room code was found in {MaxCodeAttempts} attempts.");
    }

    public async Task<RoomResult> JoinAsync(Player player, RoomCode code, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // Room first, then player: the same order everywhere, so concurrent joins cannot deadlock.
        var room = await db.LockRoomAsync(code, cancellationToken);
        if (room is null)
        {
            return new RoomResult(RoomOutcome.RoomNotFound);
        }

        // A player who left a started match can't come back (DEC-025); they fall through to the status refusal.
        if (room.IsPlaying(player.Id))
        {
            return new RoomResult(RoomOutcome.AlreadyMember, room);
        }

        switch (room.Status)
        {
            case RoomStatus.Active:
                return new RoomResult(RoomOutcome.RoomStarted);
            case not RoomStatus.Waiting:
                return new RoomResult(RoomOutcome.RoomClosed);
        }

        await LockPlayerAsync(player, cancellationToken);
        if (await OpenRoomCodeAsync(player, cancellationToken) is { } openRoom)
        {
            return new RoomResult(RoomOutcome.AlreadyInRoom, OpenRoomCode: openRoom);
        }

        if (room.IsFull)
        {
            return new RoomResult(RoomOutcome.RoomFull);
        }

        room.Join(player, timeProvider.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new RoomResult(RoomOutcome.Joined, room);
    }

    /// <summary>The room with this code if <paramref name="player"/> is a member; otherwise null, so non-members learn nothing.</summary>
    public async Task<GameRoom?> FindForMemberAsync(Player player, RoomCode code, CancellationToken cancellationToken)
    {
        var room = await db.GameRooms.AsNoTracking().SingleOrDefaultAsync(r => r.Code == code.Value, cancellationToken);
        return room is not null && room.IsMember(player.Id) ? room : null;
    }

    /// <summary>
    /// The player's open room, if any: the one to return to after a refresh, a dropped connection or on another device
    /// (STORY-027, ENG-006, DEC-029). A player has at most one (DEC-021, DEC-025).
    /// </summary>
    public Task<GameRoom?> FindOpenForPlayerAsync(Player player, CancellationToken cancellationToken) =>
        OpenRooms(player).FirstOrDefaultAsync(cancellationToken);

    private Task LockPlayerAsync(Player player, CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""SELECT 1 FROM "Players" WHERE "Id" = {player.Id} FOR UPDATE""", cancellationToken);

    private Task<string?> OpenRoomCodeAsync(Player player, CancellationToken cancellationToken) =>
        OpenRooms(player).Select(r => r.Code).FirstOrDefaultAsync(cancellationToken);

    /// <summary>Waiting rooms the player is in (as host or member) and started matches they still play (DEC-025).</summary>
    private IQueryable<GameRoom> OpenRooms(Player player) =>
        db.GameRooms.AsNoTracking()
            .Where(r => (r.Status == RoomStatus.Waiting || r.Status == RoomStatus.Active)
                && r.Members.Any(m => m.PlayerId == player.Id && m.LeftAt == null));

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
