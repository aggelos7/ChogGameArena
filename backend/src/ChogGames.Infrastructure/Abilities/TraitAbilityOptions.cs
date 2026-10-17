using ChogGames.Core.Abilities;
using ChogGames.Core.Nfts;

namespace ChogGames.Infrastructure.Abilities;

/// <summary>
/// Trait-based ability assignment (STORY-061, DEC-066), bound from <c>Abilities:TraitBased</c>. Off by default: the
/// shared pool is then the only strategy, as in MVP. The rules are checked at startup whether or not it is on.
/// </summary>
public sealed class TraitAbilityOptions
{
    public const string SectionName = "Abilities:TraitBased";

    public const int MaxRules = 200;

    public const int MaxTraitsPerRule = 10;

    public bool Enabled { get; set; }

    public List<TraitAbilityRuleOptions> Rules { get; set; } = [];

    /// <summary>The rules with normalized traits; only valid options are converted.</summary>
    public IReadOnlyList<TraitAbilityRule> ToRules() =>
    [
        .. Rules.Select(r => new TraitAbilityRule(
            r.Game,
            TraitSet.From(r.Traits.Select(t => new NftTrait(t.Type, t.Value))),
            [.. r.Abilities.Distinct(StringComparer.Ordinal)])),
    ];
}

/// <summary>A Chog with every one of <see cref="Traits"/> may choose <see cref="Abilities"/> in <see cref="Game"/>.</summary>
public sealed class TraitAbilityRuleOptions
{
    public string Game { get; set; } = string.Empty;

    public List<TraitOptions> Traits { get; set; } = [];

    public List<string> Abilities { get; set; } = [];
}

public sealed class TraitOptions
{
    public string Type { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;
}
