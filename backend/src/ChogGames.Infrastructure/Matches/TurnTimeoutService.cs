using System.Text.Json;
using ChogGames.Core.Matches;
using ChogGames.Core.Rooms;
using ChogGames.Infrastructure.Persistence;
using ChogGames.Infrastructure.Rooms;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ChogGames.Infrastructure.Matches;

/// <param name="Code">The room whose turn timed out.</param>
/// <param name="ForfeitedPlayerId">The player who forfeited by missing too many turns in a row, if any.</param>
public sealed record TimedOutTurn(string Code, Guid? ForfeitedPlayerId);

/// <summary>
/// Times out turns that ran out (STORY-025, ENG-007, DEC-027). Each overdue match is handled in its own transaction under
/// the room's row lock and checked again after locking, so a turn times out exactly once however many sweeps run, and a
/// command that wins the lock first is not overridden. The game's <see cref="IMatchRules"/> give the safe default; a game
/// without rules just skips the turn. Deadlines are stored, so turns that ran out while the server was down time out on
/// the first sweep after it starts, and the next turn gets a full timeout from then.
/// </summary>
public sealed class TurnTimeoutService(
    ChogGamesDbContext db,
    IEnumerable<IMatchRules> rules,
    IOptions<MatchOptions> options,
    IRandomSource random,
    TimeProvider timeProvider,
    ILogger<TurnTimeoutService> logger)
{
    /// <summary>Most matches handled in one sweep; the rest wait for the next.</summary>
    public const int BatchSize = 100;

    private readonly Dictionary<string, IMatchRules> _rulesByGame = rules.ToDictionary(r => r.GameType, StringComparer.Ordinal);

    /// <summary>Times out every overdue turn found now and returns the matches that changed.</summary>
    public async Task<IReadOnlyList<TimedOutTurn>> TimeOutOverdueTurnsAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var codes = await db.GameRooms.AsNoTracking()
            .Where(r => r.Status == RoomStatus.Active && r.TurnEndsAt <= now)
            .OrderBy(r => r.TurnEndsAt)
            .Select(r => r.Code)
            .Take(BatchSize)
            .ToListAsync(cancellationToken);

        var timedOut = new List<TimedOutTurn>();
        foreach (var code in codes)
        {
            try
            {
                if (await TimeOutAsync(code, now, cancellationToken) is { } turn)
                {
                    timedOut.Add(turn);
                }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                // One broken match must not hold up the others; it is tried again on the next sweep.
                logger.LogError(error, "The overdue turn of room {Code} could not be timed out.", code);
            }
            finally
            {
                db.ChangeTracker.Clear();
            }
        }

        return timedOut;
    }

    private async Task<TimedOutTurn?> TimeOutAsync(string code, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!RoomCode.TryParse(code, out var roomCode))
        {
            return null;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var room = await db.LockRoomAsync(roomCode, cancellationToken);

        // Another sweep, a command ending the turn or a forfeit may have got there first.
        if (room is null || !room.IsTurnOverdue(now))
        {
            return null;
        }

        var playerId = room.CurrentPlayerId!.Value;
        string? state = null;
        if (_rulesByGame.TryGetValue(room.GameType, out var gameRules))
        {
            using var current = room.State is null ? null : JsonDocument.Parse(room.State);
            if (gameRules.TimeOutTurn(new MatchTurnContext(room, current?.RootElement, random)) is { } safeDefault)
            {
                if (safeDefault.ValueKind != JsonValueKind.Object)
                {
                    throw new InvalidOperationException($"The {room.GameType} rules timed out a turn without a JSON object state.");
                }

                state = safeDefault.GetRawText();
            }
        }

        var forfeited = room.TimeOutTurn(state, options.Value.MaxMissedTurns, now);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new TimedOutTurn(room.Code, forfeited ? playerId : null);
    }
}
