using System.Collections.Immutable;
using System.Text.RegularExpressions;

namespace ChogGames.Core.Abilities;

/// <summary>
/// One ability a game offers (STORY-030, NFT-014, DEC-032): what it is called, whom it targets, when it may be used,
/// how often per match and the values its effect uses, such as a 25% reward boost. Definitions are versioned code in each
/// game module; the game's <see cref="AbilityCatalog"/> carries the version that matches snapshot. Nothing here depends on
/// a player's character or NFT traits (NFT-016, ADR-007). The description is a template its values fill in, so a holder's
/// stronger values read right (STORY-090, DEC-077): <c>{percent}</c> is a value, <c>{uses}</c> the uses, and
/// <c>{cards|the top card|the top # cards}</c> the first form for a value of 1, otherwise the second, with <c>#</c> as
/// the value, such as "the top 2 cards".
/// </summary>
public sealed partial record AbilityDefinition
{
    public const int MaxIdLength = 32;

    public const int MaxNameLength = 40;

    public const int MaxDescriptionLength = 200;

    /// <summary>Most uses one ability may allow per match; abilities are normally once per match (requirements §6).</summary>
    public const int MaxUsesLimit = 10;

    /// <summary>The value name that stands for <see cref="MaxUses"/>, in a description and in holder Tier values (DEC-077).</summary>
    public const string Uses = "uses";

    private AbilityDefinition(
        string id,
        string name,
        string descriptionTemplate,
        string description,
        AbilityTargetKind target,
        int maxUses,
        ImmutableArray<string> windows,
        ImmutableSortedDictionary<string, decimal> parameters)
    {
        Id = id;
        Name = name;
        DescriptionTemplate = descriptionTemplate;
        Description = description;
        Target = target;
        MaxUses = maxUses;
        Windows = windows;
        Parameters = parameters;
    }

    /// <summary>Stable id such as <c>reward_boost</c>: what players select and commands name.</summary>
    public string Id { get; }

    public string Name { get; }

    /// <summary>The description with its values filled in.</summary>
    public string Description { get; }

    /// <summary>The description as written, with its placeholders.</summary>
    public string DescriptionTemplate { get; }

    public AbilityTargetKind Target { get; }

    /// <summary>Uses allowed per match; counted by the engine from STORY-034.</summary>
    public int MaxUses { get; }

    /// <summary>The game's activation windows in which the ability may be used, such as <c>before_event</c>.</summary>
    public IReadOnlyList<string> Windows { get; }

    /// <summary>The configured values of the effect, by name, such as <c>percent</c> = 25; checked against its handler.</summary>
    public IReadOnlyDictionary<string, decimal> Parameters { get; }

    /// <summary>A definition; throws <see cref="ArgumentException"/> for anything malformed, so a bad catalog fails at startup.</summary>
    public static AbilityDefinition Create(
        string id,
        string name,
        string description,
        AbilityTargetKind target,
        IEnumerable<string> windows,
        IReadOnlyDictionary<string, decimal>? parameters = null,
        int maxUses = 1)
    {
        if (!IsValidId(id))
        {
            throw new ArgumentException($"An ability id must be lower-case snake case of 1 to {MaxIdLength} characters.", nameof(id));
        }

        if (string.IsNullOrWhiteSpace(name) || name.Length > MaxNameLength)
        {
            throw new ArgumentException($"Ability {id} needs a name of 1 to {MaxNameLength} characters.", nameof(name));
        }

        if (!Enum.IsDefined(target))
        {
            throw new ArgumentException($"Ability {id} has an unknown target kind.", nameof(target));
        }

        if (maxUses is < 1 or > MaxUsesLimit)
        {
            throw new ArgumentException($"Ability {id} must allow 1 to {MaxUsesLimit} uses per match.", nameof(maxUses));
        }

        var windowList = windows.ToImmutableArray();
        if (windowList.IsEmpty || windowList.Any(w => !IsValidId(w)) || windowList.Distinct(StringComparer.Ordinal).Count() != windowList.Length)
        {
            throw new ArgumentException($"Ability {id} needs one or more distinct activation windows in snake case.", nameof(windows));
        }

        var values = (parameters ?? ImmutableDictionary<string, decimal>.Empty).ToImmutableSortedDictionary(StringComparer.Ordinal);
        if (values.Keys.Any(k => !IsValidId(k)))
        {
            throw new ArgumentException($"Ability {id} has a parameter name that isn't snake case.", nameof(parameters));
        }

        if (values.ContainsKey(Uses))
        {
            throw new ArgumentException($"Ability {id} can't name a parameter {Uses}; that is its uses per match.", nameof(parameters));
        }

        var rendered = Render(id, description, values, maxUses);
        if (string.IsNullOrWhiteSpace(rendered) || rendered.Length > MaxDescriptionLength)
        {
            throw new ArgumentException($"Ability {id} needs a description of 1 to {MaxDescriptionLength} characters.", nameof(description));
        }

        return new AbilityDefinition(id, name, description, rendered, target, maxUses, windowList, values);
    }

    /// <summary>
    /// This ability with other values, such as a holder Tier's (STORY-090, DEC-077): the description is filled in again.
    /// Throws <see cref="ArgumentException"/> when the values don't fit the description or the uses are out of range.
    /// </summary>
    public AbilityDefinition WithValues(IReadOnlyDictionary<string, decimal> parameters, int maxUses) =>
        Create(Id, Name, DescriptionTemplate, Target, Windows, parameters, maxUses);

    /// <summary>The template with each placeholder replaced by its value; a stray brace or an unknown name is malformed.</summary>
    private static string Render(string id, string? template, IReadOnlyDictionary<string, decimal> values, int maxUses)
    {
        if (template is null)
        {
            throw new ArgumentException($"Ability {id} needs a description.", "description");
        }

        var rendered = Placeholder().Replace(template, match =>
        {
            var key = match.Groups["name"].Value;
            decimal value;
            if (key == Uses)
            {
                value = maxUses;
            }
            else if (!values.TryGetValue(key, out value))
            {
                throw new ArgumentException($"Ability {id}'s description names {key}, which it doesn't configure.", "description");
            }

            var number = value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
            return match.Groups["one"].Success
                ? (value == 1 ? match.Groups["one"].Value : match.Groups["many"].Value).Replace("#", number, StringComparison.Ordinal)
                : number;
        });
        if (rendered.Contains('{') || rendered.Contains('}'))
        {
            throw new ArgumentException($"Ability {id}'s description has a malformed placeholder.", "description");
        }

        return rendered;
    }

    /// <summary>Lower-case letters, digits and underscores, starting with a letter: ability, window and parameter names.</summary>
    public static bool IsValidId(string? id) => id is not null && IdFormat().IsMatch(id);

    [GeneratedRegex("^[a-z][a-z0-9_]{0,31}$")]
    private static partial Regex IdFormat();

    [GeneratedRegex(@"\{(?<name>[a-z][a-z0-9_]{0,31})(\|(?<one>[^{}|]+)\|(?<many>[^{}|]+))?\}")]
    private static partial Regex Placeholder();

    public bool Equals(AbilityDefinition? other) =>
        other is not null
        && (Id, Name, Description, Target, MaxUses) == (other.Id, other.Name, other.Description, other.Target, other.MaxUses)
        && Windows.SequenceEqual(other.Windows)
        && Parameters.OrderBy(p => p.Key, StringComparer.Ordinal).SequenceEqual(other.Parameters.OrderBy(p => p.Key, StringComparer.Ordinal));

    public override int GetHashCode() => HashCode.Combine(Id, Target, MaxUses);
}

/// <summary>Whom an ability acts on; the engine checks the command's target against it before the game sees it.</summary>
public enum AbilityTargetKind
{
    /// <summary>The user or the game itself, such as boosting their own reward; a command must name no target.</summary>
    None,

    /// <summary>Another player still in the match, named by seat, such as stealing their card.</summary>
    Opponent,
}
