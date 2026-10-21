using ChogGames.Core.Players;

namespace ChogGames.Core.Abilities;

/// <summary>
/// The MVP strategy (STORY-031, requirements §6, DEC-033): every player, guest or NFT holder, gets the game's whole
/// ability pool. The character is never read, so NFT rarity and traits change nothing. No longer registered since
/// <see cref="HolderAbilityStrategy"/> replaced it (STORY-090, DEC-077); kept for tests and as the plain pool.
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
