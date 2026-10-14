using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ChogGames.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMemberResults : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Result",
                table: "RoomMembers",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_RoomMembers_Result",
                table: "RoomMembers",
                sql: "\"Result\" IS NULL OR (jsonb_typeof(\"Result\") = 'object' AND \"Rank\" IS NOT NULL AND \"LeftAt\" IS NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_RoomMembers_Result",
                table: "RoomMembers");

            migrationBuilder.DropColumn(
                name: "Result",
                table: "RoomMembers");
        }
    }
}
