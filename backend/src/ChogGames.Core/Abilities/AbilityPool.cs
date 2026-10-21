using ChogGames.Core.Players;

namespace ChogGames.Core.Abilities;

/// <summary>
/// The abilities a player may choose from for a game (STORY-031, NFT-012, DEC-033): the game's catalog, filtered by the
/// registered <see cref="IAbilityAssignmentStrategy"/>. It never offers more than the catalog: whatever a strategy
/// returns that the game doesn't define, or with values outside its handler's ranges, is dropped, and so is a second
/// copy. Each ability keeps the values the strategy gave it, such as a holder Tier's (STORY-090, DEC-077). A game
/// without abilities offers none. The lobby (STORY-033, DEC-035) shows <see cref="For"/>, checks a
/// choice with <see cref="Offers"/> and locks it at start with <see cref="Lock"/>.
/// </summary>
public sealed class AbilityPool(AbilityEngine engine, IAbilityAssignmentStrategy strategy)
{
    /// <summary>A guest's or a standard Chog's character, which every pool's standard abilities are offered to.</summary>
    private static readonly CharacterSnapshot StandardChog = CharacterSnapshot.Standard(DefaultAvatar.Classic, DateTimeOffset.UnixEpoch);

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
            .Where(a => a is not null && catalog.Allows(a))
            .DistinctBy(a => a.Id, StringComparer.Ordinal)
            .ToDictionary(a => a.Id, StringComparer.Ordinal);
        return [.. catalog.Definitions.Where(d => chosen.ContainsKey(d.Id)).Select(d => chosen[d.Id])];
    }

    /// <summary>Whether <paramref name="gameType"/> has <paramref name="abilityId"/>, whoever may select it.</summary>
    public bool Defines(string gameType, string? abilityId) =>
        abilityId is not null && engine.CatalogFor(gameType)?.Contains(abilityId) == true;

    /// <summary>Whether <paramref name="abilityId"/> is one <paramref name="character"/> may select in <paramref name="gameType"/>.</summary>
    public bool Offers(string gameType, CharacterSnapshot character, string? abilityId) =>
        AbilityDefinition.IsValidId(abilityId) && For(gameType, character).Any(a => a.Id == abilityId);

    /// <summary>
    /// Whether only holders may select <paramref name="abilityId"/> in <paramref name="gameType"/>: the game has it, but a
    /// standard Chog's pool leaves it out (STORY-091, DEC-077, DEC-078). Shown so holder abilities stand out.
    /// </summary>
    public bool IsHolderOnly(string gameType, string abilityId) =>
        Defines(gameType, abilityId) && !Offers(gameType, StandardChog, abilityId);

    /// <summary>
    /// Locks <paramref name="abilityId"/> for a match that starts with <paramref name="character"/> (STORY-033, NFT-015,
    /// DEC-035): its snapshot with the catalog's version and the pool's values, or null when the pool doesn't offer it.
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
