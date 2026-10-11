using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace ChogGames.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGameRegistryAndRooms : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "GameDefinitions",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    MinPlayers = table.Column<int>(type: "integer", nullable: false),
                    MaxPlayers = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GameDefinitions", x => x.Id);
                    table.CheckConstraint("CK_GameDefinitions_Id_Format", "\"Id\" ~ '^[a-z0-9]+(-[a-z0-9]+)*$'");
                    table.CheckConstraint("CK_GameDefinitions_Players", "\"MinPlayers\" >= 2 AND \"MinPlayers\" <= \"MaxPlayers\" AND \"MaxPlayers\" <= 6");
                });

            migrationBuilder.CreateTable(
                name: "GameRooms",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character(6)", fixedLength: true, maxLength: 6, nullable: false),
                    GameType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    HostPlayerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Capacity = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GameRooms", x => x.Id);
                    table.CheckConstraint("CK_GameRooms_Capacity", "\"Capacity\" BETWEEN 2 AND 6");
                    table.CheckConstraint("CK_GameRooms_Code_Format", "\"Code\" ~ '^[2-9A-HJKMNP-Z]{6}$'");
                    table.CheckConstraint("CK_GameRooms_Status", "\"Status\" IN ('Waiting', 'Active', 'Completed', 'Cancelled')");
                    table.ForeignKey(
                        name: "FK_GameRooms_GameDefinitions_GameType",
                        column: x => x.GameType,
                        principalTable: "GameDefinitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GameRooms_Players_HostPlayerId",
                        column: x => x.HostPlayerId,
                        principalTable: "Players",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RoomMembers",
                columns: table => new
                {
                    RoomId = table.Column<Guid>(type: "uuid", nullable: false),
                    PlayerId = table.Column<Guid>(type: "uuid", nullable: false),
                    JoinedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RoomMembers", x => new { x.RoomId, x.PlayerId });
                    table.ForeignKey(
                        name: "FK_RoomMembers_GameRooms_RoomId",
                        column: x => x.RoomId,
                        principalTable: "GameRooms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_RoomMembers_Players_PlayerId",
                        column: x => x.PlayerId,
                        principalTable: "Players",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                table: "GameDefinitions",
                columns: new[] { "Id", "MaxPlayers", "MinPlayers" },
                values: new object[,]
                {
                    { "chog-life", 6, 2 },
                    { "chog-race", 6, 2 }
                });

            migrationBuilder.CreateIndex(
                name: "IX_GameRooms_Code",
                table: "GameRooms",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GameRooms_GameType",
                table: "GameRooms",
                column: "GameType");

            migrationBuilder.CreateIndex(
                name: "IX_GameRooms_HostPlayerId",
                table: "GameRooms",
                column: "HostPlayerId",
                unique: true,
                filter: "\"Status\" = 'Waiting'");

            migrationBuilder.CreateIndex(
                name: "IX_RoomMembers_PlayerId",
                table: "RoomMembers",
                column: "PlayerId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RoomMembers");

            migrationBuilder.DropTable(
                name: "GameRooms");

            migrationBuilder.DropTable(
                name: "GameDefinitions");
        }
    }
}
