using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ChogGames.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMatchCommands : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "State",
                table: "GameRooms",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "Version",
                table: "GameRooms",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateTable(
                name: "MatchActions",
                columns: table => new
                {
                    RoomId = table.Column<Guid>(type: "uuid", nullable: false),
                    ActionId = table.Column<Guid>(type: "uuid", nullable: false),
                    PlayerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Payload = table.Column<string>(type: "jsonb", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MatchActions", x => new { x.RoomId, x.ActionId });
                    table.CheckConstraint("CK_MatchActions_Payload", "jsonb_typeof(\"Payload\") = 'object'");
                    table.CheckConstraint("CK_MatchActions_Type_Format", "\"Type\" ~ '^[A-Z][A-Z0-9_]{0,31}$'");
                    table.CheckConstraint("CK_MatchActions_Version", "\"Version\" >= 1");
                    table.ForeignKey(
                        name: "FK_MatchActions_GameRooms_RoomId",
                        column: x => x.RoomId,
                        principalTable: "GameRooms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MatchActions_Players_PlayerId",
                        column: x => x.PlayerId,
                        principalTable: "Players",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_GameRooms_State",
                table: "GameRooms",
                sql: "\"State\" IS NULL OR (jsonb_typeof(\"State\") = 'object' AND \"StartedAt\" IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_GameRooms_Version",
                table: "GameRooms",
                sql: "\"Version\" >= 0");

            migrationBuilder.CreateIndex(
                name: "IX_MatchActions_PlayerId",
                table: "MatchActions",
                column: "PlayerId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MatchActions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_GameRooms_State",
                table: "GameRooms");

            migrationBuilder.DropCheckConstraint(
                name: "CK_GameRooms_Version",
                table: "GameRooms");

            migrationBuilder.DropColumn(
                name: "State",
                table: "GameRooms");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "GameRooms");
        }
    }
}
