using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ChogGames.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddLobbyReadyState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ReadyCharacterId",
                table: "RoomMembers",
                type: "character varying(78)",
                maxLength: 78,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReadyCharacterKind",
                table: "RoomMembers",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MinPlayers",
                table: "GameRooms",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // Rooms created before this migration take the minimum from their game.
            migrationBuilder.Sql("""
                UPDATE "GameRooms" AS r SET "MinPlayers" = g."MinPlayers"
                FROM "GameDefinitions" AS g WHERE g."Id" = r."GameType";
                """);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "StartedAt",
                table: "GameRooms",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_RoomMembers_Ready",
                table: "RoomMembers",
                sql: "(\"ReadyCharacterKind\" IS NULL) = (\"ReadyCharacterId\" IS NULL) AND (\"ReadyCharacterKind\" IS NULL OR \"ReadyCharacterKind\" IN ('Standard', 'Nft'))");

            migrationBuilder.AddCheckConstraint(
                name: "CK_GameRooms_MinPlayers",
                table: "GameRooms",
                sql: "\"MinPlayers\" >= 2 AND \"MinPlayers\" <= \"Capacity\"");

            migrationBuilder.AddCheckConstraint(
                name: "CK_GameRooms_StartedAt",
                table: "GameRooms",
                sql: "\"StartedAt\" IS NULL OR \"Status\" <> 'Waiting'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_RoomMembers_Ready",
                table: "RoomMembers");

            migrationBuilder.DropCheckConstraint(
                name: "CK_GameRooms_MinPlayers",
                table: "GameRooms");

            migrationBuilder.DropCheckConstraint(
                name: "CK_GameRooms_StartedAt",
                table: "GameRooms");

            migrationBuilder.DropColumn(
                name: "ReadyCharacterId",
                table: "RoomMembers");

            migrationBuilder.DropColumn(
                name: "ReadyCharacterKind",
                table: "RoomMembers");

            migrationBuilder.DropColumn(
                name: "MinPlayers",
                table: "GameRooms");

            migrationBuilder.DropColumn(
                name: "StartedAt",
                table: "GameRooms");
        }
    }
}
