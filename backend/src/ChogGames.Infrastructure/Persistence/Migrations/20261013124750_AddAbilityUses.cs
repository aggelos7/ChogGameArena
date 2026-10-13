using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ChogGames.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAbilityUses : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AbilityUses",
                table: "RoomMembers",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddCheckConstraint(
                name: "CK_RoomMembers_AbilityUses",
                table: "RoomMembers",
                sql: "\"AbilityUses\" >= 0 AND \"AbilityUses\" <= COALESCE((\"Ability\" ->> 'maxUses')::integer, 0)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_RoomMembers_AbilityUses",
                table: "RoomMembers");

            migrationBuilder.DropColumn(
                name: "AbilityUses",
                table: "RoomMembers");
        }
    }
}
