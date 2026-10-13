using System.Text.Json;
using ChogGames.Core.Abilities;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace ChogGames.Infrastructure.Persistence.Configurations;

/// <summary>
/// Stores a member's <see cref="AbilitySnapshot"/> as one JSON document (STORY-033, NFT-015, DEC-035). The stored shape
/// is its own, so renaming domain members can't silently change what earlier matches recorded.
/// </summary>
internal static class AbilitySnapshotConversion
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>EF passes nulls through without converting them, so both directions only see values.</summary>
    public static ValueConverter<AbilitySnapshot?, string> Converter { get; } = new(
        snapshot => Serialize(snapshot!),
        json => Deserialize(json));

    /// <summary>Snapshots are immutable, so comparing by value is enough to detect changes.</summary>
    public static ValueComparer<AbilitySnapshot?> Comparer { get; } = new(
        (left, right) => Equals(left, right),
        snapshot => snapshot == null ? 0 : snapshot.GetHashCode(),
        snapshot => snapshot);

    private static string Serialize(AbilitySnapshot snapshot) => JsonSerializer.Serialize(
        new StoredAbility(
            snapshot.Id,
            snapshot.CatalogVersion,
            snapshot.Name,
            snapshot.Description,
            snapshot.Target.ToString(),
            snapshot.MaxUses,
            [.. snapshot.Windows],
            new SortedDictionary<string, decimal>(snapshot.Parameters.ToDictionary(), StringComparer.Ordinal)),
        Json);

    private static AbilitySnapshot Deserialize(string json)
    {
        var stored = JsonSerializer.Deserialize<StoredAbility>(json, Json)
            ?? throw new InvalidOperationException("A stored ability snapshot is empty.");
        return AbilitySnapshot.Restore(
            stored.Id,
            stored.CatalogVersion,
            stored.Name,
            stored.Description,
            Enum.Parse<AbilityTargetKind>(stored.Target),
            stored.MaxUses,
            stored.Windows,
            stored.Parameters);
    }

    private sealed record StoredAbility(
        string Id,
        int CatalogVersion,
        string Name,
        string Description,
        string Target,
        int MaxUses,
        string[] Windows,
        SortedDictionary<string, decimal> Parameters);
}
