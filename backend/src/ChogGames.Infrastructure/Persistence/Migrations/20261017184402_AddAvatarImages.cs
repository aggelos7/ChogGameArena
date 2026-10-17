using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ChogGames.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAvatarImages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "AvatarImageId",
                table: "Players",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AvatarImages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PlayerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Data = table.Column<byte[]>(type: "bytea", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AvatarImages", x => x.Id);
                    table.CheckConstraint("CK_AvatarImages_Data", "octet_length(\"Data\") BETWEEN 1 AND 1048576");
                    table.ForeignKey(
                        name: "FK_AvatarImages_Players_PlayerId",
                        column: x => x.PlayerId,
                        principalTable: "Players",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Players_AvatarImageId",
                table: "Players",
                column: "AvatarImageId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AvatarImages_PlayerId",
                table: "AvatarImages",
                column: "PlayerId",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Players_AvatarImages_AvatarImageId",
                table: "Players",
                column: "AvatarImageId",
                principalTable: "AvatarImages",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Players_AvatarImages_AvatarImageId",
                table: "Players");

            migrationBuilder.DropTable(
                name: "AvatarImages");

            migrationBuilder.DropIndex(
                name: "IX_Players_AvatarImageId",
                table: "Players");

            migrationBuilder.DropColumn(
                name: "AvatarImageId",
                table: "Players");
        }
    }
}
