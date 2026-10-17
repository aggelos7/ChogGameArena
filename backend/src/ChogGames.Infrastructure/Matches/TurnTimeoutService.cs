using System.Text.Json;
using ChogGames.Core.Matches;
using ChogGames.Core.Rooms;
using ChogGames.Infrastructure.Persistence;
using ChogGames.Infrastructure.Progression;
using ChogGames.Infrastructure.Rooms;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ChogGames.Infrastructure.Matches;

/// <param name="Code">The room whose turn timed out, or whose match ended.</param>
/// <param name="ForfeitedPlayerId">The player who forfeited by missing too many turns in a row, if any.</param>
public sealed record TimedOutTurn(string Code, Guid? ForfeitedPlayerId);

/// <summary>
/// Times out turns that ran out (STORY-025, ENG-007, DEC-027). Each overdue match is handled in its own transaction under
/// the room's row lock and checked again after locking, so a turn times out exactly once however many sweeps run, and a
/// command that wins the lock first is not overridden. The game's <see cref="IMatchRules"/> give the safe default; a game
/// without rules just skips the turn. A reply window that runs out (STORY-054, DEC-058) gets the rules' default for the
/// reply instead, and the player who didn't reply misses a turn. Deadlines are stored, so turns that ran out while the server was down time out on
/// the first sweep after it starts, and the next turn gets a full timeout from then. The same sweep ends matches whose time
/// is up or whose players have all finished (STORY-041, ENG-011), with the standings their rules give, and records the
/// results in the same transaction, once; a match past its time limit is ended rather than having its turn timed out.
/// </summary>
public sealed class TurnTimeoutService(
    ChogGamesDbContext db,
    IEnumerable<IMatchRules> rules,
    IOptions<MatchOptions> options,
    IRandomSource random,
    TimeProvider timeProvider,
    IOptions<ProgressionOptions> progression,
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
            .Where(r => r.Status == RoomStatus.Active && (r.TurnEndsAt <= now || r.MatchEndsAt <= now))
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

        if (room is not null && room.IsDueToEnd(now))
        {
            await EndAsync(room, now, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new TimedOutTurn(room.Code, null);
        }

        // Another sweep, a command ending the turn or a forfeit may have got there first.
        if (room is null || !room.IsTurnOverdue(now))
        {
            return null;
        }

        string? state = null;
        Guid? skips = null;
        if (_rulesByGame.TryGetValue(room.GameType, out var gameRules))
        {
            using var current = room.State is null ? null : JsonDocument.Parse(room.State);
            var context = new MatchTurnContext(room, current?.RootElement, random);
            JsonElement? safeDefault;
            if (room.ReplyingPlayerId is null)
            {
                safeDefault = gameRules.TimeOutTurn(context);
            }
            else
            {
                // The reply window ran out (DEC-058): the rules' default for the reply, which may skip a player's turn.
                var reply = gameRules.TimeOutReply(context);
                if (reply is not null && reply.Outcome != MatchRuleOutcome.Accepted)
                {
                    throw new InvalidOperationException($"The {room.GameType} rules timed out a reply without accepting a default.");
                }

                if (reply is { Standings: { } standings, State: { ValueKind: JsonValueKind.Object } final })
                {
                    room.FinishOnReplyTimeout(final.GetRawText(), standings, now);
                    await db.RecordStatsAsync(room, progression.Value.Xp.ToRules(), cancellationToken);
                    await db.SaveChangesAsync(cancellationToken);
                    await transaction.CommitAsync(cancellationToken);
                    return new TimedOutTurn(room.Code, null);
                }

                safeDefault = reply?.State;
                skips = reply?.SkipsNextTurnOf;
            }

            if (safeDefault is { } json)
            {
                if (json.ValueKind != JsonValueKind.Object)
                {
                    throw new InvalidOperationException($"The {room.GameType} rules timed out a turn without a JSON object state.");
                }

                state = json.GetRawText();
            }
        }

        var forfeited = room.TimeOutTurn(state, options.Value.MaxMissedTurns, now, skips);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new TimedOutTurn(room.Code, forfeited);
    }

    /// <summary>The game's rules rank the players still playing, and the match is completed with its results recorded.</summary>
    private async Task EndAsync(GameRoom room, DateTimeOffset now, CancellationToken cancellationToken)
    {
        using var current = room.State is null ? null : JsonDocument.Parse(room.State);
        var timeUp = now >= room.MatchEndsAt;
        var ending = _rulesByGame.GetValueOrDefault(room.GameType)?.EndMatch(new MatchEndContext(room, current?.RootElement, timeUp))
            ?? throw new InvalidOperationException($"The {room.GameType} rules can't end a match.");
        if (ending.State.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException($"The {room.GameType} rules ended a match without a JSON object state.");
        }

        room.End(ending.State.GetRawText(), ending.Standings, now);
        await db.RecordStatsAsync(room, progression.Value.Xp.ToRules(), cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
    }
}
