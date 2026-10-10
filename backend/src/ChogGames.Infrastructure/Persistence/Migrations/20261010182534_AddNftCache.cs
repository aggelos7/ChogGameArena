using System;
using System.Numerics;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ChogGames.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddNftCache : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ChogNfts",
                columns: table => new
                {
                    ChainId = table.Column<long>(type: "bigint", nullable: false),
                    ContractAddress = table.Column<string>(type: "character varying(42)", maxLength: 42, nullable: false),
                    TokenId = table.Column<BigInteger>(type: "numeric(78,0)", nullable: false),
                    OwnerAddress = table.Column<string>(type: "character varying(42)", maxLength: 42, nullable: true),
                    OwnerRefreshedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    MetadataStatus = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    MetadataRefreshedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    MetadataUri = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ImageUri = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChogNfts", x => new { x.ChainId, x.ContractAddress, x.TokenId });
                    table.CheckConstraint("CK_ChogNfts_Owner_Format", "\"OwnerAddress\" IS NULL OR \"OwnerAddress\" ~ '^0x[0-9a-f]{40}$'");
                    table.CheckConstraint("CK_ChogNfts_TokenId_Uint256", "\"TokenId\" >= 0 AND \"TokenId\" < 2::numeric ^ 256");
                });

            migrationBuilder.CreateTable(
                name: "NftOwnershipScans",
                columns: table => new
                {
                    ChainId = table.Column<long>(type: "bigint", nullable: false),
                    ContractAddress = table.Column<string>(type: "character varying(42)", maxLength: 42, nullable: false),
                    WalletAddress = table.Column<string>(type: "character varying(42)", maxLength: 42, nullable: false),
                    ScannedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NftOwnershipScans", x => new { x.ChainId, x.ContractAddress, x.WalletAddress });
                });

            migrationBuilder.CreateTable(
                name: "ChogNftTraits",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    Position = table.Column<int>(type: "integer", nullable: false),
                    TraitType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    TraitValue = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ChainId = table.Column<long>(type: "bigint", nullable: false),
                    ContractAddress = table.Column<string>(type: "character varying(42)", maxLength: 42, nullable: false),
                    TokenId = table.Column<BigInteger>(type: "numeric(78,0)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChogNftTraits", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ChogNftTraits_ChogNfts_ChainId_ContractAddress_TokenId",
                        columns: x => new { x.ChainId, x.ContractAddress, x.TokenId },
                        principalTable: "ChogNfts",
                        principalColumns: new[] { "ChainId", "ContractAddress", "TokenId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ChogNfts_ChainId_ContractAddress_OwnerAddress",
                table: "ChogNfts",
                columns: new[] { "ChainId", "ContractAddress", "OwnerAddress" });

            migrationBuilder.CreateIndex(
                name: "IX_ChogNftTraits_ChainId_ContractAddress_TokenId_Position",
                table: "ChogNftTraits",
                columns: new[] { "ChainId", "ContractAddress", "TokenId", "Position" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ChogNftTraits");

            migrationBuilder.DropTable(
                name: "NftOwnershipScans");

            migrationBuilder.DropTable(
                name: "ChogNfts");
        }
    }
}
