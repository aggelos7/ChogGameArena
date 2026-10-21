using ChogGames.Core.Players;

namespace ChogGames.Core.Abilities;

/// <summary>
/// Decides which of a game's abilities a player may choose from with their character (STORY-031, NFT-012, ADR-007,
/// DEC-033). DI registers <see cref="HolderAbilityStrategy"/> (STORY-090, DEC-077), or <see cref="TraitBasedAbilityStrategy"/>,
/// built on an <see cref="ITraitAbilityResolver"/> (STORY-032, DEC-034), when <c>Abilities:TraitBased:Enabled</c> is on
/// (STORY-061, DEC-066): one registration, without changes to the engine or the games. A strategy is the only place a
/// character's traits may be read. Callers go through <see cref="AbilityPool"/>, which keeps any strategy's answer inside
/// the game's catalog and its handlers' ranges.
/// </summary>
public interface IAbilityAssignmentStrategy
{
    /// <summary>
    /// The abilities of <paramref name="catalog"/> that <paramref name="character"/> may select, in catalog order, each
    /// as defined or with the values the character gets (DEC-077).
    /// </summary>
    IReadOnlyList<AbilityDefinition> AvailableAbilities(AbilityCatalog catalog, CharacterSnapshot character);
}
