using ChogGames.Core.Players;
using ChogGames.Core.Rooms;
using ChogGames.Infrastructure.Characters;
using ChogGames.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ChogGames.Infrastructure.Rooms;

/// <summary>
/// The waiting room's lobby (STORY-021, ENG-002): members mark ready with their chosen Chog, leave (STORY-063) and the
/// host starts the match. The room's row is locked first and then the members' rows, the same order as joining, so a
/// start cannot race a join, a leave, a ready change or a Chog change. Every member's Chog is checked live at start
/// (STORY-018, DEC-022) and its snapshot stored with the member (STORY-023, DEC-025); leaving a started match is a forfeit.
/// </summary>
public sealed class LobbyService(
    ChogGamesDbContext db,
    CharacterEligibilityService eligibility,
    TimeProvider timeProvider,
    ILogger<LobbyService> logger)
{
    public async Task<RoomResult> SetReadyAsync(Player player, RoomCode code, bool ready, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var room = await db.LockRoomAsync(code, cancellationToken);
        if (room is null || !room.IsMember(player.Id))
        {
            return new RoomResult(RoomOutcome.RoomNotFound);
        }

        if (StatusFailure(room) is { } failure)
        {
            return failure;
        }

        if (ready)
        {
            // Ready is for the Chog selected now, read under the player's lock rather than from the caller's copy.
            await LockPlayersAsync([player.Id], cancellationToken);
            var current = await db.Players.AsNoTracking().SingleAsync(p => p.Id == player.Id, cancellationToken);
            room.MarkReady(current);
        }
        else
        {
            room.MarkNotReady(player.Id);
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new RoomResult(RoomOutcome.ReadyChanged, room);
    }

    /// <summary>
    /// The player leaves: a waiting room, where the host leaving cancels it for everyone (DEC-023), or a started match,
    /// which is a forfeit (DEC-025). Leaving a match already left changes nothing.
    /// </summary>
    public async Task<RoomResult> LeaveAsync(Player player, RoomCode code, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var room = await db.LockRoomAsync(code, cancellationToken);
        if (room is null || !room.IsMember(player.Id))
        {
            return new RoomResult(RoomOutcome.RoomNotFound);
        }

        if (room.Status == RoomStatus.Active && room.HasLeft(player.Id))
        {
            return new RoomResult(RoomOutcome.AlreadyLeft, room);
        }

        if (room.Status is not (RoomStatus.Waiting or RoomStatus.Active))
        {
            return new RoomResult(RoomOutcome.RoomClosed);
        }

        room.Leave(player.Id, timeProvider.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new RoomResult(RoomOutcome.Left, room);
    }

    public async Task<RoomResult> StartAsync(Player caller, RoomCode code, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var room = await db.LockRoomAsync(code, cancellationToken);
        if (room is null || !room.IsMember(caller.Id))
        {
            return new RoomResult(RoomOutcome.RoomNotFound);
        }

        var memberIds = room.Members.Select(m => m.PlayerId).ToArray();
        await LockPlayersAsync(memberIds, cancellationToken);
        var players = await db.Players.AsNoTracking().Where(p => memberIds.Contains(p.Id)).ToListAsync(cancellationToken);

        switch (room.CheckStart(caller.Id, players))
        {
            case RoomStartProblem.NotHost:
                return new RoomResult(RoomOutcome.NotHost);
            case RoomStartProblem.NotWaiting:
                return StatusFailure(room)!;
            case RoomStartProblem.NotEnoughPlayers:
                return new RoomResult(RoomOutcome.NotEnoughPlayers);
            case RoomStartProblem.PlayersNotReady:
                return new RoomResult(RoomOutcome.PlayersNotReady);
        }

        // Ownership is read live for every NFT; the snapshots are stored with the members (DEC-025).
        var characters = new Dictionary<Guid, CharacterSnapshot>();
        var notOwned = new List<Guid>();
        var unavailable = false;
        foreach (var player in players)
        {
            var result = await eligibility.SnapshotForMatchAsync(player, cancellationToken);
            switch (result.Status)
            {
                case CharacterEligibilityStatus.Eligible:
                    characters[player.Id] = result.Snapshot!;
                    break;
                case CharacterEligibilityStatus.NftNotOwned:
                    notOwned.Add(player.Id);
                    break;
                case CharacterEligibilityStatus.OwnershipUnavailable:
                    unavailable = true;
                    break;
            }
        }

        if (notOwned.Count > 0)
        {
            // They must choose another Chog, so they are no longer ready; nobody else changes.
            foreach (var playerId in notOwned)
            {
                room.MarkNotReady(playerId);
            }

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            logger.LogInformation("Room start refused: {Count} member(s) no longer own their selected Chog.", notOwned.Count);
            return new RoomResult(RoomOutcome.CharacterUnavailable);
        }

        if (unavailable)
        {
            return new RoomResult(RoomOutcome.OwnershipUnavailable);
        }

        room.Start(caller.Id, players, characters, timeProvider.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new RoomResult(RoomOutcome.Started, room);
    }

    private static RoomResult? StatusFailure(GameRoom room) => room.Status switch
    {
        RoomStatus.Waiting => null,
        RoomStatus.Active => new RoomResult(RoomOutcome.RoomStarted),
        _ => new RoomResult(RoomOutcome.RoomClosed),
    };

    /// <summary>Locks players in id order, so two transactions locking overlapping sets cannot deadlock.</summary>
    private Task LockPlayersAsync(Guid[] playerIds, CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlInterpolatedAsync(
            $"""SELECT 1 FROM (SELECT "Id" FROM "Players" WHERE "Id" = ANY({playerIds}) ORDER BY "Id" FOR UPDATE) AS locked""",
            cancellationToken);
}
