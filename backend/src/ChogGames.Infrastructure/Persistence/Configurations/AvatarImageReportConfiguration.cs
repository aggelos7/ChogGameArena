using ChogGames.Core.Players;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ChogGames.Infrastructure.Persistence.Configurations;

internal sealed class AvatarImageReportConfiguration : IEntityTypeConfiguration<AvatarImageReport>
{
    public void Configure(EntityTypeBuilder<AvatarImageReport> builder)
    {
        builder.ToTable("AvatarImageReports", t =>
        {
            t.HasCheckConstraint("CK_AvatarImageReports_Reason", "\"Reason\" IN ('Sexual', 'Hate', 'Violence', 'Scam', 'Other')");
            t.HasCheckConstraint("CK_AvatarImageReports_DismissedAfterCreated", "\"DismissedAt\" IS NULL OR \"DismissedAt\" >= \"CreatedAt\"");
        });
        // One report per reporter and photo, dismissed ones included.
        builder.HasKey(r => new { r.ImageId, r.ReporterId });
        builder.Property(r => r.Reason).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(r => r.CreatedAt).IsRequired();
        builder.HasIndex(r => r.ReporterId);
        // Reports go with the photo (replaced or removed) and with the reporter.
        builder.HasOne<AvatarImage>().WithMany().HasForeignKey(r => r.ImageId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Player>().WithMany().HasForeignKey(r => r.ReporterId).OnDelete(DeleteBehavior.Cascade);
    }
}
