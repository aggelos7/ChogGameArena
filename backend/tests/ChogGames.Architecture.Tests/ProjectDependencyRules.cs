using System.Xml.Linq;

namespace ChogGames.Architecture.Tests;

/// <summary>
/// Allowed project-to-project references for the modular monolith (docs/architecture.md).
/// Every production project must be listed; a reference not listed here is a violation.
/// </summary>
public static class ProjectDependencyRules
{
    public static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> Allowed =
        new Dictionary<string, IReadOnlySet<string>>
        {
            ["ChogGames.Core"] = new HashSet<string>(),
            ["ChogGames.Games.ChogLife"] = new HashSet<string> { "ChogGames.Core" },
            ["ChogGames.Games.ChogRace"] = new HashSet<string> { "ChogGames.Core" },
            ["ChogGames.Games.ChogRoyale"] = new HashSet<string> { "ChogGames.Core" },
            ["ChogGames.Infrastructure"] = new HashSet<string> { "ChogGames.Core" },
            ["ChogGames.Api"] = new HashSet<string>
            {
                "ChogGames.Core",
                "ChogGames.Infrastructure",
                "ChogGames.Games.ChogLife",
                "ChogGames.Games.ChogRace",
                "ChogGames.Games.ChogRoyale",
            },
        };

    public static IReadOnlyList<string> FindViolations(IReadOnlyDictionary<string, IReadOnlyCollection<string>> actualReferences)
    {
        var violations = new List<string>();
        foreach (var (project, references) in actualReferences)
        {
            if (!Allowed.TryGetValue(project, out var allowed))
            {
                violations.Add($"{project} has no dependency rule; add it to {nameof(ProjectDependencyRules)}.");
                continue;
            }

            violations.AddRange(references
                .Where(reference => !allowed.Contains(reference))
                .Select(reference => $"{project} must not reference {reference}."));
        }

        return violations;
    }

    public static IReadOnlyCollection<string> ReadProjectReferences(string csprojPath) =>
        XDocument.Load(csprojPath)
            .Descendants("ProjectReference")
            .Select(element => Path.GetFileNameWithoutExtension(
                element.Attribute("Include")!.Value.Replace('\\', Path.DirectorySeparatorChar)))
            .ToArray();
}
