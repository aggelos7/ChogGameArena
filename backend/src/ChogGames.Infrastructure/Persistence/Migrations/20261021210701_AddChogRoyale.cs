using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ChogGames.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Chog Royale becomes a supported game for 2 to 6 players (STORY-071, DEC-082); whether rooms can be opened for it
    /// is still the catalogue's status (<c>CHOG_ROYALE_STATUS</c>).
    /// </summary>
    public partial class AddChogRoyale : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.InsertData(
                table: "GameDefinitions",
                columns: new[] { "Id", "MaxPlayers", "MinPlayers" },
                values: new object[] { "chog-royale", 6, 2 });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "GameDefinitions",
                keyColumn: "Id",
                keyValue: "chog-royale");
        }
    }
}
