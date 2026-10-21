using ChogGames.Core.Nfts;
using ChogGames.Core.Players;

namespace ChogGames.Core.Abilities;

/// <summary>
/// The holder strategy and the default (STORY-090, DEC-077), replacing <see cref="SharedAbilityPoolStrategy"/>. A holder
/// is a player whose character is a Chog NFT: at start the lobby takes that snapshot with ownership read live, so a Chog
/// sold before then never counts. Everyone may choose the game's standard abilities; holders may choose all of them.
/// Each ability comes with the values of the character's Tier, read from its Tier trait: a guest, a standard Chog, a
/// Common Chog and one without a readable Tier get the Base Tier's. Only the strategy reads the trait; the games see the
/// values, never the Tier (DEC-034).
/// </summary>
public sealed class HolderAbilityStrategy(HolderAbilityRules rules) : IAbilityAssignmentStrategy
{
    public IReadOnlyList<AbilityDefinition> AvailableAbilities(AbilityCatalog catalog, CharacterSnapshot character)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(character);
        if (!rules.Games.TryGetValue(catalog.GameType, out var game))
        {
            return catalog.Definitions;
        }

        var holder = character.Kind == CharacterKind.Nft;
        var tier = holder ? TierOf(character) : null;
        return
        [
            .. catalog.Definitions
                .Where(ability => holder || game.Standard.Contains(ability.Id))
                .Select(ability => WithTier(ability, game, tier)),
        ];
    }

    /// <summary>The character's one Tier, normalized; null without exactly one Tier trait.</summary>
    private string? TierOf(CharacterSnapshot character) =>
        TraitSet.From(character).ValuesOf(rules.TraitType) is [var tier] ? tier : null;

    /// <summary>The catalog's values, then the Base Tier's, then the character's Tier's, by name.</summary>
    private static AbilityDefinition WithTier(AbilityDefinition ability, HolderGameRules game, string? tier)
    {
        var values = ability.Parameters.ToDictionary(StringComparer.Ordinal);
        var uses = ability.MaxUses;
        foreach (var name in (string?[])[HolderAbilityRules.BaseTier, tier])
        {
            if (name is null
                || !game.Tiers.TryGetValue(name, out var abilities)
                || !abilities.TryGetValue(ability.Id, out var set))
            {
                continue;
            }

            foreach (var (key, value) in set)
            {
                if (key == AbilityDefinition.Uses)
                {
                    uses = (int)value;
                }
                else
                {
                    values[key] = value;
                }
            }
        }

        return ability.WithValues(values, uses);
    }
}
