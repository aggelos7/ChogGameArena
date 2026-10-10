using ChogGames.Core.Nfts;
using ChogGames.Core.Wallets;
using ChogGames.Infrastructure.Nfts.Cache;
using ChogGames.Infrastructure.Nfts.Metadata;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ChogGames.Infrastructure.Persistence.Configurations;

internal sealed class ChogNftConfiguration : IEntityTypeConfiguration<ChogNft>
{
    public void Configure(EntityTypeBuilder<ChogNft> builder)
    {
        builder.ToTable("ChogNfts", table =>
        {
            table.HasCheckConstraint("CK_ChogNfts_TokenId_Uint256", "\"TokenId\" >= 0 AND \"TokenId\" < 2::numeric ^ 256");
            table.HasCheckConstraint("CK_ChogNfts_Owner_Format", "\"OwnerAddress\" IS NULL OR \"OwnerAddress\" ~ '^0x[0-9a-f]{40}$'");
        });
        builder.HasKey(n => new { n.ChainId, n.ContractAddress, n.TokenId });
        builder.Property(n => n.ContractAddress).HasMaxLength(WalletAddress.Length);
        builder.Property(n => n.TokenId).HasColumnType("numeric(78,0)");
        builder.Property(n => n.OwnerAddress).HasMaxLength(WalletAddress.Length);
        builder.Property(n => n.MetadataStatus).HasConversion<string>().HasMaxLength(16);
        builder.Property(n => n.MetadataUri).HasMaxLength(ChogNft.MaxUriLength);
        builder.Property(n => n.Name).HasMaxLength(NftMetadataNormalizer.MaxNameLength);
        builder.Property(n => n.ImageUri).HasMaxLength(ChogNft.MaxUriLength);
        builder.Property(n => n.Version).IsRowVersion();
        builder.HasIndex(n => new { n.ChainId, n.ContractAddress, n.OwnerAddress });
        builder.HasMany(n => n.Traits).WithOne().HasForeignKey("ChainId", "ContractAddress", "TokenId").IsRequired().OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(n => n.Traits).AutoInclude();
    }
}

internal sealed class ChogNftTraitConfiguration : IEntityTypeConfiguration<ChogNftTrait>
{
    public void Configure(EntityTypeBuilder<ChogNftTrait> builder)
    {
        builder.ToTable("ChogNftTraits");
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).UseIdentityAlwaysColumn();
        builder.Property(t => t.TraitType).HasMaxLength(NftTrait.MaxLength);
        builder.Property(t => t.TraitValue).HasMaxLength(NftTrait.MaxLength);
        builder.Property<string>("ContractAddress").HasMaxLength(WalletAddress.Length);
        builder.Property<System.Numerics.BigInteger>("TokenId").HasColumnType("numeric(78,0)");
        builder.HasIndex("ChainId", "ContractAddress", "TokenId", nameof(ChogNftTrait.Position)).IsUnique();
    }
}

internal sealed class NftOwnershipScanConfiguration : IEntityTypeConfiguration<NftOwnershipScan>
{
    public void Configure(EntityTypeBuilder<NftOwnershipScan> builder)
    {
        builder.ToTable("NftOwnershipScans");
        builder.HasKey(s => new { s.ChainId, s.ContractAddress, s.WalletAddress });
        builder.Property(s => s.ContractAddress).HasMaxLength(WalletAddress.Length);
        builder.Property(s => s.WalletAddress).HasMaxLength(WalletAddress.Length);
    }
}
