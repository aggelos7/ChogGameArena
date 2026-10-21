using System.Text.Json;
using ChogGames.Core.Matches;
using ChogGames.Core.Rooms;
using ChogGames.Infrastructure.Persistence;
using ChogGames.Infrastructure.Progression;

namespace ChogGames.Infrastructure.Matches;

/// <summary>
/// Resolves a match's due round (STORY-070, DEC-081), for the command that locked it last and for the sweep that finds
/// its deadline passed. The caller holds the room's row lock in a transaction and saves; a round that isn't due is left
/// alone, so it resolves exactly once however many get there.
/// </summary>
internal static class MatchRounds
{
    /// <summary>Returns whether the round resolved; a match it completed has its results recorded, once.</summary>
    public static async Task<bool> ResolveIfDueAsync(
        ChogGamesDbContext db,
        GameRoom room,
        IMatchRules? rules,
        IRandomSource random,
        DateTimeOffset now,
        ProgressionOptions progression,
        CancellationToken cancellationToken)
    {
        if (!room.IsRoundDue(now))
        {
            return false;
        }

        if (rules is not IRoundMatchRules roundRules)
        {
            throw new InvalidOperationException($"The {room.GameType} rules can't resolve a round.");
        }

        using var current = room.State is null ? null : JsonDocument.Parse(room.State);
        var resolution = roundRules.ResolveRound(new MatchRoundContext(room, current?.RootElement, random));
        if (resolution.State.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException($"The {room.GameType} rules resolved a round without a JSON object state.");
        }

        room.ResolveRound(resolution.State.GetRawText(), now, roundRules.RoundTiming.OpensAfter, resolution.Standings);
        if (room.Status == RoomStatus.Completed)
        {
            await db.RecordStatsAsync(room, progression.Xp.ToRules(), cancellationToken);
        }

        return true;
    }
}
