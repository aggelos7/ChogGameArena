using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ChogGames.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddWalletSignIn : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "WalletAddress",
                table: "Players",
                type: "character varying(42)",
                maxLength: 42,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "WalletChallenges",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PlayerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Address = table.Column<string>(type: "character varying(42)", maxLength: 42, nullable: false),
                    ChainId = table.Column<long>(type: "bigint", nullable: false),
                    Nonce = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Message = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    IssuedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ConsumedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WalletChallenges", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WalletChallenges_Players_PlayerId",
                        column: x => x.PlayerId,
                        principalTable: "Players",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Players_WalletAddress",
                table: "Players",
                column: "WalletAddress",
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_Players_Wallet_MatchesKind",
                table: "Players",
                sql: "(\"Kind\" = 'Wallet') = (\"WalletAddress\" IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Players_WalletAddress_Format",
                table: "Players",
                sql: "\"WalletAddress\" ~ '^0x[0-9a-f]{40}$'");

            migrationBuilder.CreateIndex(
                name: "IX_WalletChallenges_Nonce",
                table: "WalletChallenges",
                column: "Nonce",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WalletChallenges_PlayerId",
                table: "WalletChallenges",
                column: "PlayerId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WalletChallenges");

            migrationBuilder.DropIndex(
                name: "IX_Players_WalletAddress",
                table: "Players");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Players_Wallet_MatchesKind",
                table: "Players");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Players_WalletAddress_Format",
                table: "Players");

            migrationBuilder.DropColumn(
                name: "WalletAddress",
                table: "Players");
        }
    }
}
