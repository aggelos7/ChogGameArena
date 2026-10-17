using ChogGames.Core.Players;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ChogGames.Infrastructure.Persistence.Configurations;

internal sealed class AvatarImageConfiguration : IEntityTypeConfiguration<AvatarImage>
{
    /// <summary>
    /// Above the largest WebP the allowed settings produce (pure noise at 512×512, quality 100: about 765 KiB; the
    /// defaults stay under 200 KiB), so the check only ever stops bytes that didn't come from the encoder.
    /// </summary>
    public const int MaxStoredBytes = 1024 * 1024;

    public void Configure(EntityTypeBuilder<AvatarImage> builder)
    {
        builder.ToTable("AvatarImages", t => t.HasCheckConstraint(
            "CK_AvatarImages_Data", $"octet_length(\"Data\") BETWEEN 1 AND {MaxStoredBytes}"));
        builder.HasKey(i => i.Id);
        builder.Property(i => i.Id).ValueGeneratedNever();
        builder.Property(i => i.Data).IsRequired();
        builder.Property(i => i.CreatedAt).IsRequired();
        // One uploaded avatar per player, deleted with the player.
        builder.HasIndex(i => i.PlayerId).IsUnique();
        builder.HasOne<Player>().WithOne().HasForeignKey<AvatarImage>(i => i.PlayerId).OnDelete(DeleteBehavior.Cascade);
    }
}
