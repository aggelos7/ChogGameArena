using ChogGames.Core.Abilities;
using ChogGames.Core.Nfts;

namespace ChogGames.Infrastructure.Abilities;

/// <summary>
/// Holder abilities and Tier strength (STORY-090, DEC-077), bound from <c>Abilities:Holder</c>: per game, the abilities
/// everyone may choose and, per Tier, the values that replace the catalog's. Checked at startup against the games'
/// catalogs and their handlers' ranges, so a bad value stops the API. A change applies to matches that start after it;
/// the values are locked into each match's ability snapshot (DEC-035).
/// </summary>
public sealed class HolderAbilityOptions
{
    public const string SectionName = "Abilities:Holder";

    /// <summary>The Tier name for guests, standard Chogs, Common holders and Chogs without a readable Tier.</summary>
    public const string BaseTier = "Base";

    /// <summary>The trait that names a Chog's Tier.</summary>
    public string TraitType { get; set; } = "Tier";

    /// <summary>By game id, such as <c>chog-life</c>.</summary>
    public Dictionary<string, HolderGameOptions> Games { get; set; } = [];

    /// <summary>The rules with normalized Tier names; only valid options are converted.</summary>
    public HolderAbilityRules ToRules() =>
        new(
            TraitType,
            Games.ToDictionary(
                g => g.Key,
                g => new HolderGameRules(
                    g.Value.Standard.ToHashSet(StringComparer.Ordinal),
                    g.Value.Tiers.ToDictionary(
                        t => TraitSet.Normalize(t.Key),
                        t => (IReadOnlyDictionary<string, IReadOnlyDictionary<string, decimal>>)t.Value.ToDictionary(
                            a => a.Key,
                            a => (IReadOnlyDictionary<string, decimal>)a.Value.ToDictionary(StringComparer.Ordinal),
                            StringComparer.Ordinal),
                        StringComparer.Ordinal)),
                StringComparer.Ordinal));
}

public sealed class HolderGameOptions
{
    /// <summary>The abilities everyone may choose; holders may choose every ability of the game.</summary>
    public List<string> Standard { get; set; } = [];

    /// <summary>
    /// By Tier (<see cref="HolderAbilityOptions.BaseTier"/> or a Tier trait value), then ability id, then value name: the
    /// values that replace the catalog's. <c>uses</c> sets the uses per match.
    /// </summary>
    public Dictionary<string, Dictionary<string, Dictionary<string, decimal>>> Tiers { get; set; } = [];
}
