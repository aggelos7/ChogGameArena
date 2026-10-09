using System.Text.RegularExpressions;
using ChogGames.Core.Games;
using Microsoft.Extensions.Options;

namespace ChogGames.Api.Catalog;

public sealed partial class GameCatalogOptionsValidator : IValidateOptions<GameCatalogOptions>
{
    private const string Section = GameCatalogOptions.SectionName + ":" + nameof(GameCatalogOptions.Items);

    public ValidateOptionsResult Validate(string? name, GameCatalogOptions options)
    {
        if (options.Items.Count == 0)
        {
            return ValidateOptionsResult.Fail($"Setting '{Section}' must list at least one game.");
        }

        var errors = new List<string>();
        var seenIds = new HashSet<string>(StringComparer.Ordinal);

        for (var index = 0; index < options.Items.Count; index++)
        {
            var item = options.Items[index];
            var path = $"{Section}:{index}";

            if (!GameIdPattern().IsMatch(item.Id))
            {
                errors.Add($"'{path}:Id' must be lowercase kebab-case (e.g. 'chog-life'), got '{item.Id}'.");
            }
            else if (!seenIds.Add(item.Id))
            {
                errors.Add($"'{path}:Id' duplicates game id '{item.Id}'.");
            }

            if (string.IsNullOrWhiteSpace(item.Name))
            {
                errors.Add($"'{path}:Name' is required.");
            }

            if (!TryParseStatus(item.Status, out _))
            {
                errors.Add($"'{path}:Status' must be one of {string.Join(", ", Enum.GetNames<GameStatus>())}, got '{item.Status}'.");
            }
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }

    /// <summary>Accepts only the enum names; numeric values are rejected.</summary>
    public static bool TryParseStatus(string value, out GameStatus status) =>
        Enum.TryParse(value, ignoreCase: false, out status) && Enum.IsDefined(status) && !int.TryParse(value, out _);

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex GameIdPattern();
}
