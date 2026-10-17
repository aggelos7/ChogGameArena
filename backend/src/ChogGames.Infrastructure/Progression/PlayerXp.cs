using ChogGames.Core.Progression;
using ChogGames.Core.Rooms;
using ChogGames.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace ChogGames.Infrastructure.Progression;

/// <summary>One place on a leaderboard (STORY-059, DEC-064); the player id is for the server only, never returned.</summary>
public sealed record LeaderboardEntry(int Place, Guid PlayerId, string DisplayName, string AvatarId, int Xp, int Wins);

/// <summary>A leaderboard's top places and the viewer's own place, null while they have no XP on it.</summary>
public sealed record LeaderboardStandings(IReadOnlyList<LeaderboardEntry> Top, LeaderboardEntry? Viewer);

/// <summary>
/// XP and leaderboards from verified match results (STORY-059, DEC-064). XP is stored per player and match when the
/// server records a completed match; every board adds those awards up, so a board only ever counts completed matches.
/// </summary>
public static class PlayerXp
{
    /// <summary>Places listed on a board besides the viewer's own.</summary>
    public const int TopPlaces = 50;

    /// <summary>
    /// Gives each member of <paramref name="room"/> still playing their XP under <paramref name="rules"/>, dated when the
    /// match ended. Called by <c>RecordStatsAsync</c> after the completed room is saved, in the transaction that completes
    /// it and with its players locked; a match never awards a player twice.
    /// </summary>
    internal static Task AwardXpAsync(this ChogGamesDbContext db, GameRoom room, XpRules rules, CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO "XpAwards" ("RoomId", "PlayerId", "GameType", "EarnedAt", "OpponentsBeaten", "Won", "Xp", "RulesVersion")
            SELECT r."Id", m."PlayerId", r."GameType", r."EndedAt", b.beaten, m."Rank" = 1,
                {rules.PerMatch} + {rules.PerOpponentBeaten} * b.beaten, {rules.Version}
            FROM "RoomMembers" m
            JOIN "GameRooms" r ON r."Id" = m."RoomId"
            CROSS JOIN LATERAL (
                SELECT COUNT(*)::int AS beaten FROM "RoomMembers" o WHERE o."RoomId" = m."RoomId" AND o."Rank" > m."Rank") b
            WHERE r."Id" = {room.Id} AND r."Status" = {nameof(RoomStatus.Completed)}
                AND m."LeftAt" IS NULL AND m."Rank" IS NOT NULL
            ON CONFLICT ("RoomId", "PlayerId") DO NOTHING
            """,
            cancellationToken);

    /// <summary>All the XP <paramref name="playerId"/> has earned.</summary>
    public static async Task<int> TotalXpAsync(this ChogGamesDbContext db, Guid playerId, CancellationToken cancellationToken) =>
        await db.XpAwards.Where(a => a.PlayerId == playerId).SumAsync(a => (int?)a.Xp, cancellationToken) ?? 0;

    /// <summary>
    /// A board: every game when <paramref name="gameType"/> is null, and every match when <paramref name="season"/> is.
    /// Ranked by XP, then wins, then whoever reached their total first, then the player id, so places never tie.
    /// </summary>
    public static async Task<LeaderboardStandings> LeaderboardAsync(
        this ChogGamesDbContext db, string? gameType, Season? season, Guid viewerId, CancellationToken cancellationToken)
    {
        var rows = await db.Database.SqlQueryRaw<EntryRow>(
            """
            WITH totals AS (
                SELECT "PlayerId", SUM("Xp")::int AS "Xp", (COUNT(*) FILTER (WHERE "Won"))::int AS "Wins",
                    MAX("EarnedAt") FILTER (WHERE "Xp" > 0) AS reached
                FROM "XpAwards"
                WHERE (@game IS NULL OR "GameType" = @game)
                    AND (@from IS NULL OR "EarnedAt" >= @from) AND (@to IS NULL OR "EarnedAt" < @to)
                GROUP BY "PlayerId"
                HAVING SUM("Xp") > 0
            ),
            ranked AS (
                SELECT *, (ROW_NUMBER() OVER (ORDER BY "Xp" DESC, "Wins" DESC, reached, "PlayerId"))::int AS "Place"
                FROM totals
            )
            SELECT k."Place", k."PlayerId", p."DisplayName", p."AvatarId", k."Xp", k."Wins"
            FROM ranked k
            JOIN "Players" p ON p."Id" = k."PlayerId"
            WHERE k."Place" <= @top OR k."PlayerId" = @viewer
            ORDER BY k."Place"
            """,
            new NpgsqlParameter("game", NpgsqlDbType.Varchar) { Value = (object?)gameType ?? DBNull.Value },
            new NpgsqlParameter("from", NpgsqlDbType.TimestampTz) { Value = (object?)season?.StartsAt ?? DBNull.Value },
            new NpgsqlParameter("to", NpgsqlDbType.TimestampTz) { Value = (object?)season?.EndsAt ?? DBNull.Value },
            new NpgsqlParameter("top", NpgsqlDbType.Integer) { Value = TopPlaces },
            new NpgsqlParameter("viewer", NpgsqlDbType.Uuid) { Value = viewerId })
            .ToListAsync(cancellationToken);

        var entries = rows.Select(r => new LeaderboardEntry(r.Place, r.PlayerId, r.DisplayName, r.AvatarId, r.Xp, r.Wins)).ToList();
        return new LeaderboardStandings(
            [.. entries.Where(e => e.Place <= TopPlaces)],
            entries.SingleOrDefault(e => e.PlayerId == viewerId));
    }

    private sealed class EntryRow
    {
        public int Place { get; init; }

        public Guid PlayerId { get; init; }

        public string DisplayName { get; init; } = string.Empty;

        public string AvatarId { get; init; } = string.Empty;

        public int Xp { get; init; }

        public int Wins { get; init; }
    }
}
