using System.Text.Json;
using ChogGames.Core.Nfts;
using ChogGames.Core.Players;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace ChogGames.Infrastructure.Persistence.Configurations;

/// <summary>
/// Stores a member's <see cref="CharacterSnapshot"/> as one JSON document (STORY-023, DEC-025). The stored shape is
/// its own, so renaming domain members can't silently change what earlier matches recorded.
/// </summary>
internal static class CharacterSnapshotConversion
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>EF passes nulls through without converting them, so both directions only see values.</summary>
    public static ValueConverter<CharacterSnapshot?, string> Converter { get; } = new(
        snapshot => Serialize(snapshot!),
        json => Deserialize(json));

    /// <summary>Snapshots are immutable, so comparing by value is enough to detect changes.</summary>
    public static ValueComparer<CharacterSnapshot?> Comparer { get; } = new(
        (left, right) => Equals(left, right),
        snapshot => snapshot == null ? 0 : snapshot.GetHashCode(),
        snapshot => snapshot);

    private static string Serialize(CharacterSnapshot snapshot) => JsonSerializer.Serialize(
        new StoredCharacter(
            snapshot.Kind.ToString(),
            snapshot.CharacterId,
            snapshot.Name,
            snapshot.ImageUrl?.OriginalString,
            [.. snapshot.Traits.Select(t => new StoredTrait(t.Type, t.Value))],
            snapshot.WalletAddress,
            snapshot.VerifiedAt),
        Json);

    private static CharacterSnapshot Deserialize(string json)
    {
        var stored = JsonSerializer.Deserialize<StoredCharacter>(json, Json)
            ?? throw new InvalidOperationException("A stored character snapshot is empty.");
        return CharacterSnapshot.Restore(
            Enum.Parse<CharacterKind>(stored.Kind),
            stored.Id,
            stored.Name,
            stored.ImageUrl is null ? null : new Uri(stored.ImageUrl, UriKind.Absolute),
            stored.Traits.Select(t => new NftTrait(t.Type, t.Value)),
            stored.WalletAddress,
            stored.VerifiedAt);
    }

    private sealed record StoredCharacter(
        string Kind, string Id, string Name, string? ImageUrl, StoredTrait[] Traits, string? WalletAddress, DateTimeOffset VerifiedAt);

    private sealed record StoredTrait(string Type, string Value);
}
