using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ChogGames.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCollectorHoldings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ChogsHeld",
                table: "Players",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddCheckConstraint(
                name: "CK_Players_ChogsHeld",
                table: "Players",
                sql: "\"ChogsHeld\" >= 0 AND (\"Kind\" = 'Wallet' OR \"ChogsHeld\" = 0)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Players_ChogsHeld",
                table: "Players");

            migrationBuilder.DropColumn(
                name: "ChogsHeld",
                table: "Players");
        }
    }
}
