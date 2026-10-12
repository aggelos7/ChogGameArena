using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ChogGames.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMatchResults : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Rank",
                table: "RoomMembers",
                type: "integer",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_RoomMembers_Rank",
                table: "RoomMembers",
                sql: "\"Rank\" IS NULL OR (\"Rank\" BETWEEN 1 AND 6 AND \"Seat\" IS NOT NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_RoomMembers_Rank",
                table: "RoomMembers");

            migrationBuilder.DropColumn(
                name: "Rank",
                table: "RoomMembers");
        }
    }
}
