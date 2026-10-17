using ChogGames.Core.Nfts;
using ChogGames.Core.Players;
using ChogGames.Core.Wallets;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ChogGames.Infrastructure.Persistence.Configurations;

internal sealed class PlayerConfiguration : IEntityTypeConfiguration<Player>
{
    public void Configure(EntityTypeBuilder<Player> builder)
    {
        builder.ToTable("Players");
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedNever();
        builder.Property(p => p.DisplayName).HasMaxLength(PlayerDisplayName.MaxLength).IsRequired();
        builder.Property(p => p.Kind).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(p => p.CreatedAt).IsRequired();
        builder.Property(p => p.AvatarId).HasMaxLength(DefaultAvatar.MaxIdLength).IsRequired().HasDefaultValue(DefaultAvatar.Classic.Id);
        builder.Property(p => p.GamesPlayed).IsRequired().HasDefaultValue(0);
        builder.Property(p => p.Wins).IsRequired().HasDefaultValue(0);
        builder.Property(p => p.ChogsHeld).IsRequired().HasDefaultValue(0);
        builder.Property(p => p.WalletAddress).HasMaxLength(WalletAddress.Length);
        builder.Property(p => p.SelectedCharacterKind).HasConversion<string>().HasMaxLength(16).IsRequired()
            .HasDefaultValue(CharacterKind.Standard);
        builder.Property(p => p.SelectedCharacterId).HasMaxLength(NftTokenId.MaxDigits).IsRequired().HasDefaultValue(DefaultAvatar.Classic.Id);
        builder.HasIndex(p => p.WalletAddress).IsUnique();
        builder.ToTable(t =>
        {
            t.HasCheckConstraint("CK_Players_GamesPlayed_NonNegative", "\"GamesPlayed\" >= 0");
            t.HasCheckConstraint("CK_Players_Wins_WithinGamesPlayed", "\"Wins\" >= 0 AND \"Wins\" <= \"GamesPlayed\"");
            t.HasCheckConstraint("CK_Players_ChogsHeld", "\"ChogsHeld\" >= 0 AND (\"Kind\" = 'Wallet' OR \"ChogsHeld\" = 0)");
            t.HasCheckConstraint("CK_Players_WalletAddress_Format", "\"WalletAddress\" ~ '^0x[0-9a-f]{40}$'");
            t.HasCheckConstraint("CK_Players_Wallet_MatchesKind", "(\"Kind\" = 'Wallet') = (\"WalletAddress\" IS NOT NULL)");
            t.HasCheckConstraint(
                "CK_Players_SelectedCharacter",
                "(\"SelectedCharacterKind\" = 'Standard' AND \"SelectedCharacterId\" ~ '^[a-z0-9-]{1,32}$') OR "
                + "(\"SelectedCharacterKind\" = 'Nft' AND \"Kind\" = 'Wallet' AND \"SelectedCharacterId\" ~ '^(0|[1-9][0-9]{0,77})$')");
        });
    }
}
