using ChogGames.Core.Nfts;

namespace ChogGames.Core.Abilities;

/// <summary>
/// One trait rule (STORY-061, DEC-066): in <see cref="GameType"/>, a Chog that has every one of <see cref="Traits"/>
/// may choose <see cref="AbilityIds"/>. Traits are normalized (DEC-034), so rules match however the metadata spells them.
/// </summary>
public sealed record TraitAbilityRule(string GameType, TraitSet Traits, IReadOnlyList<string> AbilityIds)
{
    /// <summary>Whether <paramref name="traits"/> has every trait of the rule.</summary>
    public bool Matches(TraitSet traits)
    {
        ArgumentNullException.ThrowIfNull(traits);
        return Traits.Traits.Count > 0 && Traits.Traits.All(traits.Traits.Contains);
    }
}
