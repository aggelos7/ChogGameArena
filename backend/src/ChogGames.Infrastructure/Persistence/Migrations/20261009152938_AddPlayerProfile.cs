using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ChogGames.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPlayerProfile : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AvatarId",
                table: "Players",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "classic");

            migrationBuilder.AddColumn<int>(
                name: "GamesPlayed",
                table: "Players",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Wins",
                table: "Players",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddCheckConstraint(
                name: "CK_Players_GamesPlayed_NonNegative",
                table: "Players",
                sql: "\"GamesPlayed\" >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Players_Wins_WithinGamesPlayed",
                table: "Players",
                sql: "\"Wins\" >= 0 AND \"Wins\" <= \"GamesPlayed\"");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Players_GamesPlayed_NonNegative",
                table: "Players");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Players_Wins_WithinGamesPlayed",
                table: "Players");

            migrationBuilder.DropColumn(
                name: "AvatarId",
                table: "Players");

            migrationBuilder.DropColumn(
                name: "GamesPlayed",
                table: "Players");

            migrationBuilder.DropColumn(
                name: "Wins",
                table: "Players");
        }
    }
}
