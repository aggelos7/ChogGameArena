using ChogGames.Core.Players;

namespace ChogGames.Core.Abilities;

/// <summary>
/// The MVP strategy and the default (STORY-031, requirements §6, DEC-033, DEC-066): every player, guest or NFT holder, gets the game's whole
/// ability pool. The character is never read, so NFT rarity and traits change nothing.
/// </summary>
public sealed class SharedAbilityPoolStrategy : IAbilityAssignmentStrategy
{
    public IReadOnlyList<AbilityDefinition> AvailableAbilities(AbilityCatalog catalog, CharacterSnapshot character)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(character);
        return catalog.Definitions;
    }
}
