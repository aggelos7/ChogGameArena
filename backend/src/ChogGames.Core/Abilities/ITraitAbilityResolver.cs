using ChogGames.Core.Nfts;

namespace ChogGames.Core.Abilities;

/// <summary>
/// The contract the trait-based ability strategy is built on (STORY-032, NFT-017, ADR-007, DEC-034).
/// <see cref="ConfiguredTraitAbilityResolver"/> implements it (STORY-061, DEC-066); <see cref="TraitBasedAbilityStrategy"/>
/// wraps it and replaces the shared strategy's one registration when the flag is on, so the engine,
/// <see cref="AbilityPool"/> and the games stay as they are. A resolver must follow these rules:
/// <list type="bullet">
/// <item>The resolver sees the game's catalog and the character's normalized <see cref="TraitSet"/>, nothing else: no
/// wallet, token id, room or match.</item>
/// <item>It is deterministic: the same catalog and traits always give the same answer.</item>
/// <item>When it returns no ability of the catalog (a guest has no traits, or the traits map to nothing), the player gets
/// the game's whole catalog, as with the shared pool, so nobody is left unable to ready.</item>
/// <item>Ids the catalog doesn't define are ignored; <see cref="AbilityPool"/> drops them anyway.</item>
/// <item>The player still selects exactly one ability, and only it has an effect (NFT-016).</item>
/// </list>
/// </summary>
public interface ITraitAbilityResolver
{
    /// <summary>The ids of <paramref name="catalog"/>'s abilities that <paramref name="traits"/> make eligible.</summary>
    IReadOnlyList<string> EligibleAbilityIds(AbilityCatalog catalog, TraitSet traits);
}
