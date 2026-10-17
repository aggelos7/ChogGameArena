using ChogGames.Core.Abilities;
using ChogGames.Core.Nfts;
using Microsoft.Extensions.Options;

namespace ChogGames.Infrastructure.Abilities;

/// <summary>
/// Checks the trait rules against the games' ability catalogs at startup (DEC-066), so a rule naming a game without
/// abilities, an ability the game doesn't define, or an unusable trait stops the API instead of silently matching nothing.
/// </summary>
public sealed class TraitAbilityOptionsValidator(AbilityEngine engine) : IValidateOptions<TraitAbilityOptions>
{
    private const string Section = TraitAbilityOptions.SectionName;

    public ValidateOptionsResult Validate(string? name, TraitAbilityOptions options)
    {
        var errors = new List<string>();
        if (options.Rules.Count > TraitAbilityOptions.MaxRules)
        {
            errors.Add($"'{Section}:Rules' must have at most {TraitAbilityOptions.MaxRules} rules.");
        }

        for (var i = 0; i < options.Rules.Count; i++)
        {
            var rule = options.Rules[i];
            var path = $"{Section}:Rules:{i}";
            if (rule is null)
            {
                errors.Add($"'{path}' must not be empty.");
                continue;
            }

            var catalog = engine.CatalogFor(rule.Game ?? string.Empty);
            if (catalog is null)
            {
                errors.Add($"'{path}:Game' '{rule.Game}' has no ability catalog.");
            }

            if (rule.Traits is not { Count: >= 1 and <= TraitAbilityOptions.MaxTraitsPerRule })
            {
                errors.Add($"'{path}:Traits' must have 1–{TraitAbilityOptions.MaxTraitsPerRule} traits.");
            }
            else if (rule.Traits.Any(t => t is null || !IsTraitText(t.Type) || !IsTraitText(t.Value)))
            {
                errors.Add($"'{path}:Traits' must each have a Type and a Value of 1–{NftTrait.MaxLength} printable characters.");
            }

            if (rule.Abilities is not { Count: > 0 })
            {
                errors.Add($"'{path}:Abilities' must name at least one ability.");
            }
            else if (catalog is not null)
            {
                errors.AddRange(rule.Abilities
                    .Where(id => id is null || !catalog.Contains(id))
                    .Select(id => $"'{path}:Abilities' '{id}' is not an ability of {catalog.GameType}."));
            }
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }

    private static bool IsTraitText(string? text) =>
        text?.Trim() is { Length: > 0 and <= NftTrait.MaxLength } trimmed && !trimmed.Any(char.IsControl);
}
