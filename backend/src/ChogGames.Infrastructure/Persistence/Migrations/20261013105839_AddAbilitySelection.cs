using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ChogGames.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAbilitySelection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Ability",
                table: "RoomMembers",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AbilityId",
                table: "RoomMembers",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_RoomMembers_Ability",
                table: "RoomMembers",
                sql: "\"Ability\" IS NULL OR (jsonb_typeof(\"Ability\") = 'object' AND \"Character\" IS NOT NULL AND \"AbilityId\" IS NOT NULL AND \"Ability\" ->> 'id' IS NOT DISTINCT FROM \"AbilityId\")");

            migrationBuilder.AddCheckConstraint(
                name: "CK_RoomMembers_AbilityId",
                table: "RoomMembers",
                sql: "\"AbilityId\" IS NULL OR \"AbilityId\" ~ '^[a-z][a-z0-9_]{0,31}$'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_RoomMembers_Ability",
                table: "RoomMembers");

            migrationBuilder.DropCheckConstraint(
                name: "CK_RoomMembers_AbilityId",
                table: "RoomMembers");

            migrationBuilder.DropColumn(
                name: "Ability",
                table: "RoomMembers");

            migrationBuilder.DropColumn(
                name: "AbilityId",
                table: "RoomMembers");
        }
    }
}
