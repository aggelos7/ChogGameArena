using System.Text.RegularExpressions;

namespace ChogGames.Architecture.Tests;

/// <summary>
/// Where NFT traits may be read (STORY-032, ADR-007, DEC-034). Game modules never touch characters or traits; they see
/// players as ids and seats. The shared match and ability engine in Core never reads traits either: only an
/// <c>IAbilityAssignmentStrategy</c> may, through a trait resolver. Comment lines are ignored.
/// </summary>
public static partial class TraitAccessRules
{
    /// <summary>Anything a game module must not mention: the NFT namespace, characters, traits and the resolver.</summary>
    public static readonly IReadOnlyList<Regex> GameModuleForbidden =
    [
        Token(@"ChogGames\.Core\.Nfts"),
        Token("CharacterSnapshot"),
        Token("NftTrait"),
        Token("TraitSet"),
        Token("NormalizedTrait"),
        Token("ITraitAbilityResolver"),
        Token(@"\.Traits"),
    ];

    /// <summary>What the shared engine folders of Core must not mention: trait types or a character's traits.</summary>
    public static readonly IReadOnlyList<Regex> EngineForbidden =
    [
        Token("NftTrait"),
        Token("TraitSet"),
        Token("NormalizedTrait"),
        Token(@"\.Traits"),
    ];

    /// <summary>One violation per forbidden token found in <paramref name="source"/>, outside comment lines.</summary>
    public static IReadOnlyList<string> FindViolations(
        string file, string source, IReadOnlyList<Regex> forbidden, string reason = "read NFT traits")
    {
        var violations = new List<string>();
        var lines = source.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            violations.AddRange(forbidden
                .Where(rule => rule.IsMatch(line))
                .Select(rule => $"{file}:{i + 1} must not {reason} ({rule})."));
        }

        return violations;
    }

    /// <summary>The pattern as a whole name: not part of a longer identifier (a leading <c>\.</c> matches member access).</summary>
    internal static Regex Token(string pattern) =>
        new($@"{(pattern.StartsWith('\\') ? "" : "(?<![A-Za-z0-9_])")}{pattern}(?![A-Za-z0-9_])", RegexOptions.CultureInvariant);
}
