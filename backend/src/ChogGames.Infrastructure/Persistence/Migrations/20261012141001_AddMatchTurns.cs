using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ChogGames.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMatchTurns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "MissedTurns",
                table: "RoomMembers",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Seat",
                table: "RoomMembers",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "CurrentPlayerId",
                table: "GameRooms",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "TurnEndsAt",
                table: "GameRooms",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TurnNumber",
                table: "GameRooms",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<TimeSpan>(
                name: "TurnTimeout",
                table: "GameRooms",
                type: "interval",
                nullable: true);

            // Matches started before turns existed: seat their members in turn order (the host, then by joining), give
            // them the default turn timeout, and start a fresh first turn in each active one (DEC-027).
            migrationBuilder.Sql("""
                UPDATE "RoomMembers" AS m SET "Seat" = seated."Seat"
                FROM (
                    SELECT rm."RoomId", rm."PlayerId",
                        row_number() OVER (
                            PARTITION BY rm."RoomId" ORDER BY rm."PlayerId" <> r."HostPlayerId", rm."JoinedAt", rm."PlayerId") AS "Seat"
                    FROM "RoomMembers" AS rm JOIN "GameRooms" AS r ON r."Id" = rm."RoomId"
                    WHERE r."StartedAt" IS NOT NULL
                ) AS seated
                WHERE m."RoomId" = seated."RoomId" AND m."PlayerId" = seated."PlayerId";

                UPDATE "GameRooms" SET "TurnTimeout" = interval '20 seconds', "TurnNumber" = 1 WHERE "StartedAt" IS NOT NULL;

                UPDATE "GameRooms" AS r SET
                    "CurrentPlayerId" = (
                        SELECT m."PlayerId" FROM "RoomMembers" AS m
                        WHERE m."RoomId" = r."Id" AND m."LeftAt" IS NULL ORDER BY m."Seat" LIMIT 1),
                    "TurnEndsAt" = now() + interval '20 seconds'
                WHERE r."Status" = 'Active';
                """);

            migrationBuilder.CreateIndex(
                name: "IX_RoomMembers_RoomId_Seat",
                table: "RoomMembers",
                columns: new[] { "RoomId", "Seat" },
                unique: true,
                filter: "\"Seat\" IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_RoomMembers_MissedTurns",
                table: "RoomMembers",
                sql: "\"MissedTurns\" >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_RoomMembers_Seat",
                table: "RoomMembers",
                sql: "\"Seat\" IS NULL OR \"Seat\" BETWEEN 1 AND 6");

            migrationBuilder.CreateIndex(
                name: "IX_GameRooms_TurnEndsAt",
                table: "GameRooms",
                column: "TurnEndsAt",
                filter: "\"TurnEndsAt\" IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_GameRooms_CurrentTurn",
                table: "GameRooms",
                sql: "(\"CurrentPlayerId\" IS NULL) = (\"Status\" <> 'Active') AND (\"TurnEndsAt\" IS NULL) = (\"CurrentPlayerId\" IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_GameRooms_TurnNumber",
                table: "GameRooms",
                sql: "\"TurnNumber\" >= 0 AND (\"TurnNumber\" = 0) = (\"StartedAt\" IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_GameRooms_TurnTimeout",
                table: "GameRooms",
                sql: "(\"TurnTimeout\" IS NULL) = (\"StartedAt\" IS NULL) AND (\"TurnTimeout\" IS NULL OR \"TurnTimeout\" > interval '0')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_RoomMembers_RoomId_Seat",
                table: "RoomMembers");

            migrationBuilder.DropCheckConstraint(
                name: "CK_RoomMembers_MissedTurns",
                table: "RoomMembers");

            migrationBuilder.DropCheckConstraint(
                name: "CK_RoomMembers_Seat",
                table: "RoomMembers");

            migrationBuilder.DropIndex(
                name: "IX_GameRooms_TurnEndsAt",
                table: "GameRooms");

            migrationBuilder.DropCheckConstraint(
                name: "CK_GameRooms_CurrentTurn",
                table: "GameRooms");

            migrationBuilder.DropCheckConstraint(
                name: "CK_GameRooms_TurnNumber",
                table: "GameRooms");

            migrationBuilder.DropCheckConstraint(
                name: "CK_GameRooms_TurnTimeout",
                table: "GameRooms");

            migrationBuilder.DropColumn(
                name: "MissedTurns",
                table: "RoomMembers");

            migrationBuilder.DropColumn(
                name: "Seat",
                table: "RoomMembers");

            migrationBuilder.DropColumn(
                name: "CurrentPlayerId",
                table: "GameRooms");

            migrationBuilder.DropColumn(
                name: "TurnEndsAt",
                table: "GameRooms");

            migrationBuilder.DropColumn(
                name: "TurnNumber",
                table: "GameRooms");

            migrationBuilder.DropColumn(
                name: "TurnTimeout",
                table: "GameRooms");
        }
    }
}
