using ChogGames.Core.Games;
using ChogGames.Core.Nfts;
using ChogGames.Core.Players;
using ChogGames.Core.Rooms;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ChogGames.Infrastructure.Persistence.Configurations;

internal sealed class GameDefinitionConfiguration : IEntityTypeConfiguration<GameDefinition>
{
    public void Configure(EntityTypeBuilder<GameDefinition> builder)
    {
        builder.ToTable("GameDefinitions", table =>
        {
            table.HasCheckConstraint("CK_GameDefinitions_Id_Format", "\"Id\" ~ '^[a-z0-9]+(-[a-z0-9]+)*$'");
            table.HasCheckConstraint(
                "CK_GameDefinitions_Players",
                $"\"MinPlayers\" >= {GameDefinition.MinRoomPlayers} AND \"MinPlayers\" <= \"MaxPlayers\" AND \"MaxPlayers\" <= {GameDefinition.MaxRoomPlayers}");
        });
        builder.HasKey(g => g.Id);
        builder.Property(g => g.Id).HasMaxLength(GameDefinition.MaxIdLength);

        // Supported games are added by reviewed migrations; enabling one is configuration (Games:Items).
        builder.HasData(
            new { Id = GameDefinition.ChogLife, MinPlayers = 2, MaxPlayers = 6 },
            new { Id = GameDefinition.ChogRace, MinPlayers = 2, MaxPlayers = 6 });
    }
}

internal sealed class GameRoomConfiguration : IEntityTypeConfiguration<GameRoom>
{
    public void Configure(EntityTypeBuilder<GameRoom> builder)
    {
        var statuses = string.Join(", ", Enum.GetNames<RoomStatus>().Select(name => $"'{name}'"));
        builder.ToTable("GameRooms", table =>
        {
            table.HasCheckConstraint("CK_GameRooms_Code_Format", "\"Code\" ~ '^[2-9A-HJKMNP-Z]{6}$'");
            table.HasCheckConstraint(
                "CK_GameRooms_Capacity",
                $"\"Capacity\" BETWEEN {GameDefinition.MinRoomPlayers} AND {GameDefinition.MaxRoomPlayers}");
            table.HasCheckConstraint("CK_GameRooms_Status", $"\"Status\" IN ({statuses})");
            table.HasCheckConstraint("CK_GameRooms_MinPlayers", $"\"MinPlayers\" >= {GameDefinition.MinRoomPlayers} AND \"MinPlayers\" <= \"Capacity\"");

            // Started exactly when the room left Waiting other than by being cancelled; ended exactly in a final status (DEC-025).
            table.HasCheckConstraint(
                "CK_GameRooms_StartedAt",
                $"(\"StartedAt\" IS NULL) = (\"Status\" IN ('{nameof(RoomStatus.Waiting)}', '{nameof(RoomStatus.Cancelled)}'))");
            table.HasCheckConstraint(
                "CK_GameRooms_EndedAt",
                $"(\"EndedAt\" IS NULL) = (\"Status\" IN ('{nameof(RoomStatus.Waiting)}', '{nameof(RoomStatus.Active)}'))");
        });
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).ValueGeneratedNever();
        builder.Property(r => r.Code).HasMaxLength(RoomCode.Length).IsFixedLength();
        builder.Property(r => r.GameType).HasMaxLength(GameDefinition.MaxIdLength);
        builder.Property(r => r.Status).HasConversion<string>().HasMaxLength(16);
        builder.HasIndex(r => r.Code).IsUnique();

        // One open room per host (DEC-020).
        builder.HasIndex(r => r.HostPlayerId).IsUnique().HasFilter($"\"Status\" = '{nameof(RoomStatus.Waiting)}'");

        builder.HasOne<GameDefinition>().WithMany().HasForeignKey(r => r.GameType).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Player>().WithMany().HasForeignKey(r => r.HostPlayerId).OnDelete(DeleteBehavior.Restrict);
        builder.HasMany(r => r.Members).WithOne().HasForeignKey(m => m.RoomId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(r => r.Members).AutoInclude();
    }
}

internal sealed class RoomMemberConfiguration : IEntityTypeConfiguration<RoomMember>
{
    public void Configure(EntityTypeBuilder<RoomMember> builder)
    {
        builder.ToTable("RoomMembers", table =>
        {
            table.HasCheckConstraint(
                "CK_RoomMembers_Ready",
                "(\"ReadyCharacterKind\" IS NULL) = (\"ReadyCharacterId\" IS NULL) "
                + "AND (\"ReadyCharacterKind\" IS NULL OR \"ReadyCharacterKind\" IN ('Standard', 'Nft'))");

            // A snapshot is a JSON object. It isn't required to leave: matches started before STORY-023 have none (DEC-025).
            table.HasCheckConstraint("CK_RoomMembers_Character", "\"Character\" IS NULL OR jsonb_typeof(\"Character\") = 'object'");
        });
        builder.HasKey(m => new { m.RoomId, m.PlayerId });
        builder.Property(m => m.ReadyCharacterKind).HasConversion<string>().HasMaxLength(16);
        builder.Property(m => m.ReadyCharacterId).HasMaxLength(NftTokenId.MaxDigits);
        builder.Property(m => m.Character)
            .HasColumnType("jsonb")
            .HasConversion(CharacterSnapshotConversion.Converter, CharacterSnapshotConversion.Comparer);
        builder.HasIndex(m => m.PlayerId);
        builder.HasOne<Player>().WithMany().HasForeignKey(m => m.PlayerId).OnDelete(DeleteBehavior.Restrict);
    }
}
