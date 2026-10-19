using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ChogGames.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPhotoReportsAndTerms : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "TermsAcceptedAt",
                table: "Players",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TermsVersion",
                table: "Players",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "HiddenAt",
                table: "AvatarImages",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AvatarImageReports",
                columns: table => new
                {
                    ImageId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReporterId = table.Column<Guid>(type: "uuid", nullable: false),
                    Reason = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DismissedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AvatarImageReports", x => new { x.ImageId, x.ReporterId });
                    table.CheckConstraint("CK_AvatarImageReports_DismissedAfterCreated", "\"DismissedAt\" IS NULL OR \"DismissedAt\" >= \"CreatedAt\"");
                    table.CheckConstraint("CK_AvatarImageReports_Reason", "\"Reason\" IN ('Sexual', 'Hate', 'Violence', 'Scam', 'Other')");
                    table.ForeignKey(
                        name: "FK_AvatarImageReports_AvatarImages_ImageId",
                        column: x => x.ImageId,
                        principalTable: "AvatarImages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AvatarImageReports_Players_ReporterId",
                        column: x => x.ReporterId,
                        principalTable: "Players",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_Players_TermsAccepted",
                table: "Players",
                sql: "(\"TermsVersion\" IS NULL) = (\"TermsAcceptedAt\" IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_AvatarImages_HiddenAfterCreated",
                table: "AvatarImages",
                sql: "\"HiddenAt\" IS NULL OR \"HiddenAt\" >= \"CreatedAt\"");

            migrationBuilder.CreateIndex(
                name: "IX_AvatarImageReports_ReporterId",
                table: "AvatarImageReports",
                column: "ReporterId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AvatarImageReports");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Players_TermsAccepted",
                table: "Players");

            migrationBuilder.DropCheckConstraint(
                name: "CK_AvatarImages_HiddenAfterCreated",
                table: "AvatarImages");

            migrationBuilder.DropColumn(
                name: "TermsAcceptedAt",
                table: "Players");

            migrationBuilder.DropColumn(
                name: "TermsVersion",
                table: "Players");

            migrationBuilder.DropColumn(
                name: "HiddenAt",
                table: "AvatarImages");
        }
    }
}
