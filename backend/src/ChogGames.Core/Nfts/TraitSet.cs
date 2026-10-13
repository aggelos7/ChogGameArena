using System.Collections.Immutable;
using System.Text;
using ChogGames.Core.Players;

namespace ChogGames.Core.Nfts;

/// <summary>
/// A character's traits in the form a future trait-based ability resolver matches on (STORY-032, NFT-010, NFT-017,
/// DEC-034). Each type and value is trimmed, inner whitespace is collapsed to one space, and both are lower-cased
/// (invariant), so <c>Rarity: Legendary</c> and <c>rarity :  legendary</c> are the same trait. Duplicates are removed
/// and the set is sorted. Standard Chogs have no traits. The display text in <see cref="NftTrait"/> is never changed;
/// this is only for matching. Game modules never read traits (ADR-007).
/// </summary>
public sealed class TraitSet : IEquatable<TraitSet>
{
    private TraitSet(ImmutableArray<NormalizedTrait> traits) => Traits = traits;

    public static TraitSet Empty { get; } = new([]);

    /// <summary>The distinct traits, sorted by type, then value (ordinal).</summary>
    public IReadOnlyList<NormalizedTrait> Traits { get; }

    public static TraitSet From(CharacterSnapshot character)
    {
        ArgumentNullException.ThrowIfNull(character);
        return From(character.Traits);
    }

    public static TraitSet From(IEnumerable<NftTrait> traits)
    {
        ArgumentNullException.ThrowIfNull(traits);
        var normalized = traits
            .Select(t => t ?? throw new ArgumentException("A trait list must not contain null.", nameof(traits)))
            .Select(t => new NormalizedTrait(Normalize(t.Type), Normalize(t.Value)))
            .Distinct()
            .OrderBy(t => t.Type, StringComparer.Ordinal)
            .ThenBy(t => t.Value, StringComparer.Ordinal)
            .ToImmutableArray();
        return normalized.IsEmpty ? Empty : new TraitSet(normalized);
    }

    /// <summary>Whether the set has this trait, compared after the same normalization.</summary>
    public bool Has(string type, string value) =>
        Traits.Contains(new NormalizedTrait(Normalize(type), Normalize(value)));

    /// <summary>The values of one trait type, such as every <c>background</c>; empty when the type is missing.</summary>
    public IReadOnlyList<string> ValuesOf(string type)
    {
        var key = Normalize(type);
        return [.. Traits.Where(t => t.Type == key).Select(t => t.Value)];
    }

    /// <summary>Trimmed, inner whitespace collapsed to one space, lower-cased with the invariant culture.</summary>
    public static string Normalize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var builder = new StringBuilder(text.Length);
        foreach (var part in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (builder.Length > 0)
            {
                builder.Append(' ');
            }

            builder.Append(part);
        }

        return builder.ToString().ToLowerInvariant();
    }

    public bool Equals(TraitSet? other) => other is not null && Traits.SequenceEqual(other.Traits);

    public override bool Equals(object? obj) => Equals(obj as TraitSet);

    public override int GetHashCode() => Traits.Aggregate(0, HashCode.Combine);
}

/// <summary>One trait after <see cref="TraitSet.Normalize"/>, such as <c>rarity</c> = <c>legendary</c>.</summary>
public sealed record NormalizedTrait(string Type, string Value);
