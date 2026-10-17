using ChogGames.Core.Nfts;
using ChogGames.Core.Players;

namespace ChogGames.Core.Abilities;

/// <summary>
/// The trait-based strategy (STORY-061, NFT-017, ADR-007, DEC-066), registered instead of
/// <see cref="SharedAbilityPoolStrategy"/> only when <c>Abilities:TraitBased:Enabled</c> is on. It normalizes the
/// character's traits, asks the resolver, and keeps the catalog's abilities it names. When it names none (a guest, a
/// standard Chog, or traits no rule maps), the player gets the whole catalog, as with the shared pool (DEC-034).
/// </summary>
public sealed class TraitBasedAbilityStrategy(ITraitAbilityResolver resolver) : IAbilityAssignmentStrategy
{
    public IReadOnlyList<AbilityDefinition> AvailableAbilities(AbilityCatalog catalog, CharacterSnapshot character)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(character);
        var eligible = resolver.EligibleAbilityIds(catalog, TraitSet.From(character)).ToHashSet(StringComparer.Ordinal);
        var chosen = catalog.Definitions.Where(d => eligible.Contains(d.Id)).ToList();
        return chosen.Count == 0 ? catalog.Definitions : chosen;
    }
}
