using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ChogGames.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMatchLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_GameRooms_StartedAt",
                table: "GameRooms");

            migrationBuilder.DropCheckConstraint(
                name: "CK_GameRooms_Status",
                table: "GameRooms");

            migrationBuilder.AddColumn<string>(
                name: "Character",
                table: "RoomMembers",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LeftAt",
                table: "RoomMembers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "EndedAt",
                table: "GameRooms",
                type: "timestamp with time zone",
                nullable: true);

            // A cancelled room that had started is an abandoned match (rolling this migration back turns Abandoned into Cancelled).
            migrationBuilder.Sql(
                "UPDATE \"GameRooms\" SET \"Status\" = 'Abandoned' WHERE \"Status\" = 'Cancelled' AND \"StartedAt\" IS NOT NULL;");

            // Rooms that already ended get an end time before it is required: the best known is when they started or were created.
            migrationBuilder.Sql(
                "UPDATE \"GameRooms\" SET \"EndedAt\" = COALESCE(\"StartedAt\", \"CreatedAt\") WHERE \"Status\" IN ('Completed', 'Cancelled', 'Abandoned');");

            migrationBuilder.AddCheckConstraint(
                name: "CK_RoomMembers_Character",
                table: "RoomMembers",
                sql: "\"Character\" IS NULL OR jsonb_typeof(\"Character\") = 'object'");

            migrationBuilder.AddCheckConstraint(
                name: "CK_GameRooms_EndedAt",
                table: "GameRooms",
                sql: "(\"EndedAt\" IS NULL) = (\"Status\" IN ('Waiting', 'Active'))");

            migrationBuilder.AddCheckConstraint(
                name: "CK_GameRooms_StartedAt",
                table: "GameRooms",
                sql: "(\"StartedAt\" IS NULL) = (\"Status\" IN ('Waiting', 'Cancelled'))");

            migrationBuilder.AddCheckConstraint(
                name: "CK_GameRooms_Status",
                table: "GameRooms",
                sql: "\"Status\" IN ('Waiting', 'Active', 'Completed', 'Cancelled', 'Abandoned')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_RoomMembers_Character",
                table: "RoomMembers");

            migrationBuilder.DropCheckConstraint(
                name: "CK_GameRooms_EndedAt",
                table: "GameRooms");

            migrationBuilder.DropCheckConstraint(
                name: "CK_GameRooms_StartedAt",
                table: "GameRooms");

            migrationBuilder.DropCheckConstraint(
                name: "CK_GameRooms_Status",
                table: "GameRooms");

            // The earlier schema has no Abandoned status; such matches are kept as Cancelled.
            migrationBuilder.Sql("UPDATE \"GameRooms\" SET \"Status\" = 'Cancelled' WHERE \"Status\" = 'Abandoned';");

            migrationBuilder.DropColumn(
                name: "Character",
                table: "RoomMembers");

            migrationBuilder.DropColumn(
                name: "LeftAt",
                table: "RoomMembers");

            migrationBuilder.DropColumn(
                name: "EndedAt",
                table: "GameRooms");

            migrationBuilder.AddCheckConstraint(
                name: "CK_GameRooms_StartedAt",
                table: "GameRooms",
                sql: "\"StartedAt\" IS NULL OR \"Status\" <> 'Waiting'");

            migrationBuilder.AddCheckConstraint(
                name: "CK_GameRooms_Status",
                table: "GameRooms",
                sql: "\"Status\" IN ('Waiting', 'Active', 'Completed', 'Cancelled')");
        }
    }
}
