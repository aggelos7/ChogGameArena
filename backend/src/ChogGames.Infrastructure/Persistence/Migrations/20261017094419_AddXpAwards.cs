using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ChogGames.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddXpAwards : Migration
    {
        /// <summary>
        /// Gives XP rules version 1 (10 per match, 5 per member ranked below) to every player still playing in the
        /// completed matches already played (STORY-059, DEC-064), dated when each match ended, as if it had been awarded
        /// then. Players who left and abandoned matches earn nothing. Public so tests can compare it with live awards.
        /// </summary>
        public const string BackfillSql =
            """
            INSERT INTO "XpAwards" ("RoomId", "PlayerId", "GameType", "EarnedAt", "OpponentsBeaten", "Won", "Xp", "RulesVersion")
            SELECT r."Id", m."PlayerId", r."GameType", r."EndedAt", b.beaten, m."Rank" = 1, 10 + 5 * b.beaten, 1
            FROM "RoomMembers" m
            JOIN "GameRooms" r ON r."Id" = m."RoomId"
            CROSS JOIN LATERAL (
                SELECT COUNT(*)::int AS beaten FROM "RoomMembers" o WHERE o."RoomId" = m."RoomId" AND o."Rank" > m."Rank") b
            WHERE r."Status" = 'Completed' AND m."LeftAt" IS NULL AND m."Rank" IS NOT NULL
            ON CONFLICT ("RoomId", "PlayerId") DO NOTHING
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "XpAwards",
                columns: table => new
                {
                    RoomId = table.Column<Guid>(type: "uuid", nullable: false),
                    PlayerId = table.Column<Guid>(type: "uuid", nullable: false),
                    GameType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    EarnedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    OpponentsBeaten = table.Column<int>(type: "integer", nullable: false),
                    Won = table.Column<bool>(type: "boolean", nullable: false),
                    Xp = table.Column<int>(type: "integer", nullable: false),
                    RulesVersion = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_XpAwards", x => new { x.RoomId, x.PlayerId });
                    table.CheckConstraint("CK_XpAwards_OpponentsBeaten_Range", "\"OpponentsBeaten\" >= 0 AND \"OpponentsBeaten\" < 6");
                    table.CheckConstraint("CK_XpAwards_RulesVersion_Positive", "\"RulesVersion\" >= 1");
                    table.CheckConstraint("CK_XpAwards_Xp_Range", "\"Xp\" >= 0 AND \"Xp\" <= 6000");
                    table.ForeignKey(
                        name: "FK_XpAwards_GameRooms_RoomId",
                        column: x => x.RoomId,
                        principalTable: "GameRooms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_XpAwards_Players_PlayerId",
                        column: x => x.PlayerId,
                        principalTable: "Players",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_XpAwards_EarnedAt",
                table: "XpAwards",
                column: "EarnedAt");

            migrationBuilder.CreateIndex(
                name: "IX_XpAwards_GameType_EarnedAt",
                table: "XpAwards",
                columns: new[] { "GameType", "EarnedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_XpAwards_PlayerId",
                table: "XpAwards",
                column: "PlayerId");

            migrationBuilder.Sql(BackfillSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "XpAwards");
        }
    }
}
