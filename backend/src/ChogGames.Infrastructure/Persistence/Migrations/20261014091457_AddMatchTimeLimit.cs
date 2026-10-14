using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ChogGames.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMatchTimeLimit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "FinishedAt",
                table: "RoomMembers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "MatchEndsAt",
                table: "GameRooms",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_RoomMembers_FinishedAt",
                table: "RoomMembers",
                sql: "\"FinishedAt\" IS NULL OR \"Seat\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_GameRooms_MatchEndsAt",
                table: "GameRooms",
                column: "MatchEndsAt",
                filter: "\"Status\" = 'Active' AND \"MatchEndsAt\" IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_GameRooms_MatchEndsAt",
                table: "GameRooms",
                sql: "\"MatchEndsAt\" IS NULL OR (\"StartedAt\" IS NOT NULL AND \"MatchEndsAt\" > \"StartedAt\" AND \"MatchEndsAt\" <= \"StartedAt\" + interval '20 minutes')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_RoomMembers_FinishedAt",
                table: "RoomMembers");

            migrationBuilder.DropIndex(
                name: "IX_GameRooms_MatchEndsAt",
                table: "GameRooms");

            migrationBuilder.DropCheckConstraint(
                name: "CK_GameRooms_MatchEndsAt",
                table: "GameRooms");

            migrationBuilder.DropColumn(
                name: "FinishedAt",
                table: "RoomMembers");

            migrationBuilder.DropColumn(
                name: "MatchEndsAt",
                table: "GameRooms");
        }
    }
}
