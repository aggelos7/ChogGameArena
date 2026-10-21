namespace ChogGames.Core.Abilities;

/// <summary>
/// Who may choose which abilities and with what values (STORY-090, DEC-077), from <c>Abilities:Holder</c>. Tier names
/// are normalized like traits (DEC-034); <see cref="BaseTier"/> holds the values of guests, standard Chogs, Common
/// holders and Chogs without a readable Tier.
/// </summary>
/// <param name="TraitType">The trait that names a Chog's Tier, such as <c>Tier</c>.</param>
/// <param name="Games">By game id; a game not listed offers its whole catalog, as defined, to everyone.</param>
public sealed record HolderAbilityRules(string TraitType, IReadOnlyDictionary<string, HolderGameRules> Games)
{
    public const string BaseTier = "base";
}

/// <param name="Standard">The abilities everyone may choose; holders may choose every ability of the game.</param>
/// <param name="Tiers">
/// By normalized Tier, then ability id: the values that replace the catalog's, by name. <see cref="AbilityDefinition.Uses"/>
/// sets the uses per match. A Tier without a value takes the Base Tier's, and the Base Tier without one the catalog's.
/// </param>
public sealed record HolderGameRules(
    IReadOnlySet<string> Standard,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyDictionary<string, decimal>>> Tiers);
