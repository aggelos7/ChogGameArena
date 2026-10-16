using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ChogGames.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMatchReplies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "SkipsNextTurn",
                table: "RoomMembers",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "ReplyingPlayerId",
                table: "GameRooms",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_RoomMembers_SkipsNextTurn",
                table: "RoomMembers",
                sql: "NOT \"SkipsNextTurn\" OR \"Seat\" IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_GameRooms_ReplyingPlayer",
                table: "GameRooms",
                sql: "\"ReplyingPlayerId\" IS NULL OR (\"Status\" = 'Active' AND \"CurrentPlayerId\" IS NOT NULL AND \"ReplyingPlayerId\" <> \"CurrentPlayerId\")");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_RoomMembers_SkipsNextTurn",
                table: "RoomMembers");

            migrationBuilder.DropCheckConstraint(
                name: "CK_GameRooms_ReplyingPlayer",
                table: "GameRooms");

            migrationBuilder.DropColumn(
                name: "SkipsNextTurn",
                table: "RoomMembers");

            migrationBuilder.DropColumn(
                name: "ReplyingPlayerId",
                table: "GameRooms");
        }
    }
}
