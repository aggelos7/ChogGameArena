using System.Collections.Immutable;

namespace ChogGames.Core.Abilities;

/// <summary>
/// The ability a player locked for one match (STORY-033, NFT-015, ADR-008, DEC-035): a copy of its definition and the
/// catalog version it came from, taken at start. Later catalog changes never alter it; the next match takes a new one.
/// How many uses are left is counted from STORY-034.
/// </summary>
public sealed record AbilitySnapshot
{
    private AbilitySnapshot(
        string id,
        int catalogVersion,
        string name,
        string description,
        AbilityTargetKind target,
        int maxUses,
        ImmutableArray<string> windows,
        ImmutableSortedDictionary<string, decimal> parameters)
    {
        Id = id;
        CatalogVersion = catalogVersion;
        Name = name;
        Description = description;
        Target = target;
        MaxUses = maxUses;
        Windows = windows;
        Parameters = parameters;
    }

    public string Id { get; }

    /// <summary>The <see cref="AbilityCatalog.Version"/> the ability was locked from.</summary>
    public int CatalogVersion { get; }

    public string Name { get; }

    public string Description { get; }

    public AbilityTargetKind Target { get; }

    public int MaxUses { get; }

    public IReadOnlyList<string> Windows { get; }

    public IReadOnlyDictionary<string, decimal> Parameters { get; }

    /// <summary>Locks <paramref name="ability"/> as <paramref name="catalog"/> defines it now.</summary>
    public static AbilitySnapshot Lock(AbilityCatalog catalog, AbilityDefinition ability)
    {
        if (!catalog.TryFind(ability.Id, out var defined, out _) || !defined.Equals(ability))
        {
            throw new ArgumentException($"The {catalog.GameType} catalog doesn't define {ability.Id} like this.", nameof(ability));
        }

        return new AbilitySnapshot(
            defined.Id,
            catalog.Version,
            defined.Name,
            defined.Description,
            defined.Target,
            defined.MaxUses,
            [.. defined.Windows],
            defined.Parameters.ToImmutableSortedDictionary(StringComparer.Ordinal));
    }

    /// <summary>Rebuilds a snapshot exactly as it was stored with its match; never a new lookup in the catalog.</summary>
    public static AbilitySnapshot Restore(
        string id,
        int catalogVersion,
        string name,
        string description,
        AbilityTargetKind target,
        int maxUses,
        IEnumerable<string> windows,
        IReadOnlyDictionary<string, decimal> parameters) =>
        new(id, catalogVersion, name, description, target, maxUses, [.. windows], parameters.ToImmutableSortedDictionary(StringComparer.Ordinal));

    public bool Equals(AbilitySnapshot? other) =>
        other is not null
        && (Id, CatalogVersion, Name, Description, Target, MaxUses) == (other.Id, other.CatalogVersion, other.Name, other.Description, other.Target, other.MaxUses)
        && Windows.SequenceEqual(other.Windows)
        && Parameters.OrderBy(p => p.Key, StringComparer.Ordinal).SequenceEqual(other.Parameters.OrderBy(p => p.Key, StringComparer.Ordinal));

    public override int GetHashCode() => HashCode.Combine(Id, CatalogVersion, MaxUses);
}
