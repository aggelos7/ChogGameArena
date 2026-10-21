using ChogGames.Core.Abilities;
using ChogGames.Core.Matches;
using ChogGames.Core.Nfts;
using Microsoft.Extensions.Options;

namespace ChogGames.Infrastructure.Abilities;

/// <summary>
/// Checks <c>Abilities:Holder</c> against the games' ability catalogs at startup (STORY-090, DEC-077): an unknown game,
/// an ability or value its game doesn't define, a value outside its handler's range or not a whole number,
/// uses outside 1 to <see cref="AbilityDefinition.MaxUsesLimit"/>, or a game without a standard ability stops the API
/// instead of failing in a lobby or a match. A game this host runs without abilities is skipped: it offers none.
/// </summary>
public sealed class HolderAbilityOptionsValidator(AbilityEngine engine, IEnumerable<IMatchRules> games)
    : IValidateOptions<HolderAbilityOptions>
{
    private const string Section = HolderAbilityOptions.SectionName;

    public ValidateOptionsResult Validate(string? name, HolderAbilityOptions options)
    {
        var errors = new List<string>();
        if (!IsTraitText(options.TraitType))
        {
            errors.Add($"'{Section}:TraitType' must have 1–{NftTrait.MaxLength} printable characters.");
        }

        foreach (var (gameType, game) in options.Games ?? [])
        {
            var path = $"{Section}:Games:{gameType}";
            if (engine.CatalogFor(gameType) is not { } catalog)
            {
                if (!games.Any(g => g.GameType == gameType))
                {
                    errors.Add($"'{path}' '{gameType}' is not a game.");
                }

                continue;
            }

            if (game is null)
            {
                errors.Add($"'{path}' must not be empty.");
                continue;
            }

            if (game.Standard is not { Count: > 0 })
            {
                errors.Add($"'{path}:Standard' must name at least one ability, so everyone can ready.");
            }
            else
            {
                errors.AddRange(game.Standard
                    .Where(id => id is null || !catalog.Contains(id))
                    .Select(id => $"'{path}:Standard' '{id}' is not an ability of {gameType}."));
                if (game.Standard.Distinct(StringComparer.Ordinal).Count() != game.Standard.Count)
                {
                    errors.Add($"'{path}:Standard' names an ability twice.");
                }
            }

            var tiers = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (tier, abilities) in game.Tiers ?? [])
            {
                var tierPath = $"{path}:Tiers:{tier}";
                if (!IsTraitText(tier) || !tiers.Add(TraitSet.Normalize(tier)))
                {
                    errors.Add($"'{tierPath}' must be a distinct Tier name of 1–{NftTrait.MaxLength} printable characters.");
                }

                foreach (var (abilityId, values) in abilities ?? [])
                {
                    errors.AddRange(AbilityProblems(catalog, $"{tierPath}:{abilityId}", abilityId, values));
                }
            }
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }

    private static IEnumerable<string> AbilityProblems(
        AbilityCatalog catalog, string path, string abilityId, Dictionary<string, decimal>? values)
    {
        if (!catalog.TryFind(abilityId, out var definition, out var handler))
        {
            yield return $"'{path}' '{abilityId}' is not an ability of {catalog.GameType}.";
            yield break;
        }

        var parameters = handler.Parameters.ToDictionary(p => p.Name, StringComparer.Ordinal);
        var merged = definition.Parameters.ToDictionary(StringComparer.Ordinal);
        var uses = definition.MaxUses;
        var valid = true;
        foreach (var (key, value) in values ?? [])
        {
            if (value != decimal.Truncate(value))
            {
                yield return $"'{path}:{key}' must be a whole number.";
                valid = false;
            }
            else if (key == AbilityDefinition.Uses)
            {
                if (value is < 1 or > AbilityDefinition.MaxUsesLimit)
                {
                    yield return $"'{path}:{key}' must be 1 to {AbilityDefinition.MaxUsesLimit}.";
                    valid = false;
                }
                else
                {
                    uses = (int)value;
                }
            }
            else if (!parameters.TryGetValue(key, out var parameter))
            {
                yield return $"'{path}:{key}' is not a value of {abilityId}.";
                valid = false;
            }
            else if (value < parameter.Min || value > parameter.Max)
            {
                yield return $"'{path}:{key}' must be {parameter.Min} to {parameter.Max}.";
                valid = false;
            }
            else
            {
                merged[key] = value;
            }
        }

        if (valid && !Describes(definition, merged, uses))
        {
            yield return $"'{path}' makes the description of {abilityId} longer than {AbilityDefinition.MaxDescriptionLength} characters.";
        }
    }

    private static bool Describes(AbilityDefinition definition, Dictionary<string, decimal> values, int uses)
    {
        try
        {
            definition.WithValues(values, uses);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool IsTraitText(string? text) =>
        text?.Trim() is { Length: > 0 and <= NftTrait.MaxLength } trimmed && !trimmed.Any(char.IsControl);
}
