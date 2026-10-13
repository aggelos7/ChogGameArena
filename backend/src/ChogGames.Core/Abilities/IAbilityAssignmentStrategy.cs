using ChogGames.Core.Players;

namespace ChogGames.Core.Abilities;

/// <summary>
/// Decides which of a game's abilities a player may choose from with their character (STORY-031, NFT-012, ADR-007,
/// DEC-033). MVP registers only <see cref="SharedAbilityPoolStrategy"/>; a future trait-based strategy replaces it in
/// one registration, without changes to the engine or the games. Callers go through <see cref="AbilityPool"/>, which
/// keeps any strategy's answer inside the game's catalog.
/// </summary>
public interface IAbilityAssignmentStrategy
{
    /// <summary>The abilities of <paramref name="catalog"/> that <paramref name="character"/> may select, in catalog order.</summary>
    IReadOnlyList<AbilityDefinition> AvailableAbilities(AbilityCatalog catalog, CharacterSnapshot character);
}
