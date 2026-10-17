using ChogGames.Core.Nfts;

namespace ChogGames.Core.Abilities;

/// <summary>
/// The configured trait resolver (STORY-061, NFT-017, DEC-066): a rule matches when the Chog has all of its traits, and
/// a Chog matching several rules gets the union of their abilities. Only the game's own rules count. It returns nothing
/// when no rule matches, and the strategy then gives the whole catalog (DEC-034).
/// </summary>
public sealed class ConfiguredTraitAbilityResolver(IEnumerable<TraitAbilityRule> rules) : ITraitAbilityResolver
{
    private readonly TraitAbilityRule[] _rules = [.. rules];

    public IReadOnlyList<string> EligibleAbilityIds(AbilityCatalog catalog, TraitSet traits)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(traits);
        return [.. _rules
            .Where(r => r.GameType == catalog.GameType && r.Matches(traits))
            .SelectMany(r => r.AbilityIds)
            .Distinct(StringComparer.Ordinal)];
    }
}
