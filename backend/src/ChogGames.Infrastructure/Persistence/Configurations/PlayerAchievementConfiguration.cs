using ChogGames.Core.Achievements;
using ChogGames.Core.Players;
using ChogGames.Core.Rooms;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ChogGames.Infrastructure.Persistence.Configurations;

internal sealed class PlayerAchievementConfiguration : IEntityTypeConfiguration<PlayerAchievement>
{
    public void Configure(EntityTypeBuilder<PlayerAchievement> builder)
    {
        builder.ToTable("PlayerAchievements", table =>
            table.HasCheckConstraint("CK_PlayerAchievements_AchievementId_Format", "\"AchievementId\" ~ '^[a-z][a-z0-9_]{0,31}$'"));

        // An achievement is unlocked once per player (DEC-063).
        builder.HasKey(a => new { a.PlayerId, a.AchievementId });
        builder.Property(a => a.AchievementId).HasMaxLength(Achievement.MaxIdLength);
        builder.Property(a => a.UnlockedAt).IsRequired();
        builder.HasOne<Player>().WithMany().HasForeignKey(a => a.PlayerId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<GameRoom>().WithMany().HasForeignKey(a => a.RoomId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(a => a.RoomId);
    }
}
