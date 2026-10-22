using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ChogGames.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Simultaneous rounds in the shared engine (STORY-070, DEC-081). Every existing match is played in turns, so
    /// <c>Play</c> is backfilled with <c>Turns</c> before its check is added. Rolling back fails while a match played in
    /// rounds is active, since it has no current player.
    /// </summary>
    public partial class AddMatchRounds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_GameRooms_CurrentTurn",
                table: "GameRooms");

            migrationBuilder.AddColumn<bool>(
                name: "RoundLocked",
                table: "RoomMembers",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "RoundSubmitted",
                table: "RoomMembers",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "Play",
                table: "GameRooms",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Turns");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RoundOpensAt",
                table: "GameRooms",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "RoundVersion",
                table: "GameRooms",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_RoomMembers_Round",
                table: "RoomMembers",
                sql: "(NOT \"RoundSubmitted\" OR \"Seat\" IS NOT NULL) AND (NOT \"RoundLocked\" OR \"RoundSubmitted\")");

            migrationBuilder.AddCheckConstraint(
                name: "CK_GameRooms_CurrentTurn",
                table: "GameRooms",
                sql: "(\"CurrentPlayerId\" IS NULL) = (\"Status\" <> 'Active' OR \"Play\" = 'Rounds') AND (\"TurnEndsAt\" IS NULL) = (\"Status\" <> 'Active')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_GameRooms_Play",
                table: "GameRooms",
                sql: "\"Play\" IN ('Turns', 'Rounds')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_GameRooms_Round",
                table: "GameRooms",
                sql: "(\"RoundOpensAt\" IS NULL) = (\"Status\" <> 'Active' OR \"Play\" <> 'Rounds') AND (\"RoundVersion\" IS NULL) = (\"RoundOpensAt\" IS NULL) AND (\"RoundOpensAt\" IS NULL OR (\"RoundOpensAt\" <= \"TurnEndsAt\" AND \"RoundVersion\" <= \"Version\"))");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_RoomMembers_Round",
                table: "RoomMembers");

            migrationBuilder.DropCheckConstraint(
                name: "CK_GameRooms_CurrentTurn",
                table: "GameRooms");

            migrationBuilder.DropCheckConstraint(
                name: "CK_GameRooms_Play",
                table: "GameRooms");

            migrationBuilder.DropCheckConstraint(
                name: "CK_GameRooms_Round",
                table: "GameRooms");

            migrationBuilder.DropColumn(
                name: "RoundLocked",
                table: "RoomMembers");

            migrationBuilder.DropColumn(
                name: "RoundSubmitted",
                table: "RoomMembers");

            migrationBuilder.DropColumn(
                name: "Play",
                table: "GameRooms");

            migrationBuilder.DropColumn(
                name: "RoundOpensAt",
                table: "GameRooms");

            migrationBuilder.DropColumn(
                name: "RoundVersion",
                table: "GameRooms");

            migrationBuilder.AddCheckConstraint(
                name: "CK_GameRooms_CurrentTurn",
                table: "GameRooms",
                sql: "(\"CurrentPlayerId\" IS NULL) = (\"Status\" <> 'Active') AND (\"TurnEndsAt\" IS NULL) = (\"CurrentPlayerId\" IS NULL)");
        }
    }
}
