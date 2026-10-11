using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ChogGames.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPlayerSelectedCharacter : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SelectedCharacterId",
                table: "Players",
                type: "character varying(78)",
                maxLength: 78,
                nullable: false,
                defaultValue: "classic");

            migrationBuilder.AddColumn<string>(
                name: "SelectedCharacterKind",
                table: "Players",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Standard");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Players_SelectedCharacter",
                table: "Players",
                sql: "(\"SelectedCharacterKind\" = 'Standard' AND \"SelectedCharacterId\" ~ '^[a-z0-9-]{1,32}$') OR (\"SelectedCharacterKind\" = 'Nft' AND \"Kind\" = 'Wallet' AND \"SelectedCharacterId\" ~ '^(0|[1-9][0-9]{0,77})$')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Players_SelectedCharacter",
                table: "Players");

            migrationBuilder.DropColumn(
                name: "SelectedCharacterId",
                table: "Players");

            migrationBuilder.DropColumn(
                name: "SelectedCharacterKind",
                table: "Players");
        }
    }
}
