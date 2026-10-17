using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ChogGames.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPlayerAchievements : Migration
    {
        /// <summary>
        /// Unlocks catalog version 1 from the completed matches already played (STORY-058, DEC-063), each achievement
        /// dated by and linked to the match that earned it, as if it had been awarded when that match ended. The totals
        /// are those the server keeps: every member of a completed match played it, rank 1 won it, and a result's
        /// numeric <c>degenScore</c> (0–100) counts towards Full Degen. Public so tests can compare it with live awards.
        /// </summary>
        public const string BackfillSql =
            """
            WITH played AS (
                SELECT m."PlayerId", r."Id" AS "RoomId", r."EndedAt", r."GameType",
                    ROW_NUMBER() OVER (PARTITION BY m."PlayerId" ORDER BY r."EndedAt", r."Id") AS games_played,
                    COUNT(*) FILTER (WHERE m."Rank" = 1)
                        OVER (PARTITION BY m."PlayerId" ORDER BY r."EndedAt", r."Id" ROWS UNBOUNDED PRECEDING) AS wins,
                    ROW_NUMBER() OVER (PARTITION BY m."PlayerId", r."GameType" ORDER BY r."EndedAt", r."Id") AS in_game,
                    CASE WHEN jsonb_typeof(m."Result" -> 'degenScore') = 'number'
                        THEN LEAST(GREATEST((m."Result" ->> 'degenScore')::numeric, 0), 100)::int END AS degen_score
                FROM "RoomMembers" m
                JOIN "GameRooms" r ON r."Id" = m."RoomId"
                WHERE r."Status" = 'Completed'
            ),
            totals AS (
                SELECT "PlayerId", "RoomId", "EndedAt", games_played, wins,
                    COUNT(*) FILTER (WHERE in_game = 1)
                        OVER (PARTITION BY "PlayerId" ORDER BY "EndedAt", "RoomId" ROWS UNBOUNDED PRECEDING) AS games_tried,
                    COALESCE(MAX(degen_score)
                        OVER (PARTITION BY "PlayerId" ORDER BY "EndedAt", "RoomId" ROWS UNBOUNDED PRECEDING), 0) AS degen_score
                FROM played
            ),
            catalog (id, goal, target) AS (
                VALUES ('first_match', 'played', 1), ('regular', 'played', 10), ('veteran', 'played', 50),
                    ('first_win', 'wins', 1), ('champion', 'wins', 10),
                    ('explorer', 'tried', 2), ('full_degen', 'degen', 100)
            )
            INSERT INTO "PlayerAchievements" ("PlayerId", "AchievementId", "RoomId", "UnlockedAt")
            SELECT DISTINCT ON (t."PlayerId", c.id) t."PlayerId", c.id, t."RoomId", t."EndedAt"
            FROM totals t
            JOIN catalog c ON CASE c.goal
                WHEN 'played' THEN t.games_played
                WHEN 'wins' THEN t.wins
                WHEN 'tried' THEN t.games_tried
                ELSE t.degen_score END >= c.target
            ORDER BY t."PlayerId", c.id, t."EndedAt", t."RoomId"
            ON CONFLICT ("PlayerId", "AchievementId") DO NOTHING
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PlayerAchievements",
                columns: table => new
                {
                    PlayerId = table.Column<Guid>(type: "uuid", nullable: false),
                    AchievementId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    RoomId = table.Column<Guid>(type: "uuid", nullable: false),
                    UnlockedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlayerAchievements", x => new { x.PlayerId, x.AchievementId });
                    table.CheckConstraint("CK_PlayerAchievements_AchievementId_Format", "\"AchievementId\" ~ '^[a-z][a-z0-9_]{0,31}$'");
                    table.ForeignKey(
                        name: "FK_PlayerAchievements_GameRooms_RoomId",
                        column: x => x.RoomId,
                        principalTable: "GameRooms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PlayerAchievements_Players_PlayerId",
                        column: x => x.PlayerId,
                        principalTable: "Players",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PlayerAchievements_RoomId",
                table: "PlayerAchievements",
                column: "RoomId");

            migrationBuilder.Sql(BackfillSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PlayerAchievements");
        }
    }
}
