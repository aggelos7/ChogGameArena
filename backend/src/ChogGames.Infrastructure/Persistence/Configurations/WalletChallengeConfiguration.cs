using ChogGames.Core.Players;
using ChogGames.Core.Wallets;
using ChogGames.Infrastructure.Wallets;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ChogGames.Infrastructure.Persistence.Configurations;

internal sealed class WalletChallengeConfiguration : IEntityTypeConfiguration<WalletChallenge>
{
    public void Configure(EntityTypeBuilder<WalletChallenge> builder)
    {
        builder.ToTable("WalletChallenges");
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).ValueGeneratedNever();
        builder.Property(c => c.Address).HasMaxLength(WalletAddress.Length).IsRequired();
        builder.Property(c => c.Nonce).HasMaxLength(WalletChallenge.NonceLength).IsRequired();
        builder.Property(c => c.Message).HasMaxLength(WalletChallenge.MaxMessageLength).IsRequired();
        builder.HasIndex(c => c.Nonce).IsUnique();
        builder.HasIndex(c => c.PlayerId);
        builder.HasOne<Player>().WithMany().HasForeignKey(c => c.PlayerId).OnDelete(DeleteBehavior.Cascade);
    }
}
