using ChogGames.Core.Players;

namespace ChogGames.Core.Abilities;

/// <summary>
/// The abilities a player may choose from for a game (STORY-031, NFT-012, DEC-033): the game's catalog, filtered by the
/// registered <see cref="IAbilityAssignmentStrategy"/>. It never offers more than the catalog: whatever a strategy
/// returns that the game doesn't define, or returns twice, is dropped, and each ability comes back as the catalog
/// defines it. A game without abilities offers none. The lobby (STORY-033, DEC-035) shows <see cref="For"/>, checks a
/// choice with <see cref="Offers"/> and locks it at start with <see cref="Lock"/>.
/// </summary>
public sealed class AbilityPool(AbilityEngine engine, IAbilityAssignmentStrategy strategy)
{
    /// <summary>The abilities <paramref name="character"/> may select in <paramref name="gameType"/>, in catalog order.</summary>
    public IReadOnlyList<AbilityDefinition> For(string gameType, CharacterSnapshot character)
    {
        ArgumentNullException.ThrowIfNull(gameType);
        ArgumentNullException.ThrowIfNull(character);
        if (engine.CatalogFor(gameType) is not { } catalog)
        {
            return [];
        }

        var chosen = strategy.AvailableAbilities(catalog, character)
            .Where(a => a is not null)
            .Select(a => a.Id)
            .ToHashSet(StringComparer.Ordinal);
        return [.. catalog.Definitions.Where(d => chosen.Contains(d.Id))];
    }

    /// <summary>Whether <paramref name="abilityId"/> is one <paramref name="character"/> may select in <paramref name="gameType"/>.</summary>
    public bool Offers(string gameType, CharacterSnapshot character, string? abilityId) =>
        AbilityDefinition.IsValidId(abilityId) && For(gameType, character).Any(a => a.Id == abilityId);

    /// <summary>
    /// Locks <paramref name="abilityId"/> for a match that starts with <paramref name="character"/> (STORY-033, NFT-015,
    /// DEC-035): its snapshot with the catalog's version, or null when the pool doesn't offer it.
    /// </summary>
    public AbilitySnapshot? Lock(string gameType, CharacterSnapshot character, string? abilityId)
    {
        if (!Offers(gameType, character, abilityId))
        {
            return null;
        }

        var catalog = engine.CatalogFor(gameType)!;
        return AbilitySnapshot.Lock(catalog, For(gameType, character).Single(a => a.Id == abilityId));
    }
}
