using ChogGames.Core.Games;
using ChogGames.Core.Players;
using ChogGames.Core.Progression;
using ChogGames.Core.Rooms;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ChogGames.Infrastructure.Persistence.Configurations;

internal sealed class XpAwardConfiguration : IEntityTypeConfiguration<XpAward>
{
    public void Configure(EntityTypeBuilder<XpAward> builder)
    {
        builder.ToTable("XpAwards", table =>
        {
            table.HasCheckConstraint("CK_XpAwards_Xp_Range", $"\"Xp\" >= 0 AND \"Xp\" <= {XpRules.MaxValue * GameDefinition.MaxRoomPlayers}");
            table.HasCheckConstraint("CK_XpAwards_OpponentsBeaten_Range", $"\"OpponentsBeaten\" >= 0 AND \"OpponentsBeaten\" < {GameDefinition.MaxRoomPlayers}");
            table.HasCheckConstraint("CK_XpAwards_RulesVersion_Positive", "\"RulesVersion\" >= 1");
        });

        // A player earns XP once per match (DEC-064).
        builder.HasKey(a => new { a.RoomId, a.PlayerId });
        builder.Property(a => a.GameType).HasMaxLength(GameDefinition.MaxIdLength);
        builder.HasOne<Player>().WithMany().HasForeignKey(a => a.PlayerId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<GameRoom>().WithMany().HasForeignKey(a => a.RoomId).OnDelete(DeleteBehavior.Restrict);

        // The boards filter by game and season, and the profile adds up one player's XP.
        builder.HasIndex(a => new { a.GameType, a.EarnedAt });
        builder.HasIndex(a => a.EarnedAt);
        builder.HasIndex(a => a.PlayerId);
    }
}
