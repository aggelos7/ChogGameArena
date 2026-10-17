using System.Text.RegularExpressions;

namespace ChogGames.Architecture.Tests;

/// <summary>
/// STORY-060, DEC-065: collector tiers are cosmetic. How many Chogs a player holds never reaches a game module, the
/// shared match and ability engine, or the code that seats, starts and settles matches, so owning more NFTs cannot change
/// abilities or win chances.
/// </summary>
public class CollectorAccessTests
{
    private static readonly string Src = Path.Combine(FindBackendRoot(), "src");

    /// <summary>Everything that decides a match: Core's engine and Infrastructure's lobby, match and result code.</summary>
    private static readonly string[] MatchFolders =
    [
        Path.Combine("ChogGames.Core", "Abilities"),
        Path.Combine("ChogGames.Core", "Games"),
        Path.Combine("ChogGames.Core", "Matches"),
        Path.Combine("ChogGames.Core", "Rooms"),
        Path.Combine("ChogGames.Infrastructure", "Characters"),
        Path.Combine("ChogGames.Infrastructure", "Matches"),
        Path.Combine("ChogGames.Infrastructure", "Rooms"),
    ];

    private static readonly IReadOnlyList<Regex> Forbidden =
    [
        TraitAccessRules.Token(@"ChogGames\.Core\.Collectors"),
        TraitAccessRules.Token("CollectorTier"),
        TraitAccessRules.Token("ChogsHeld"),
        TraitAccessRules.Token("RecordChogsHeld"),
    ];

    private static string FindBackendRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ChogGames.sln")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Could not locate backend/ChogGames.sln from the test output directory.");
    }

    private static IEnumerable<string> SourceFiles(string directory) =>
        Directory.GetFiles(directory, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

    private static IReadOnlyList<string> Violations(IEnumerable<string> files) =>
        [.. files.SelectMany(path => TraitAccessRules.FindViolations(
            Path.GetRelativePath(Src, path), File.ReadAllText(path), Forbidden, "read collector holdings"))];

    [Fact]
    public void No_game_module_reads_collector_holdings()
    {
        var files = Directory.GetDirectories(Src, "ChogGames.Games.*").SelectMany(SourceFiles).ToList();

        Assert.NotEmpty(files);
        Assert.Empty(Violations(files));
    }

    [Fact]
    public void Nothing_that_decides_a_match_reads_collector_holdings()
    {
        var files = MatchFolders.SelectMany(folder => SourceFiles(Path.Combine(Src, folder))).ToList();

        Assert.Contains(files, path => Path.GetFileName(path) == "SharedAbilityPoolStrategy.cs");
        Assert.Contains(files, path => Path.GetFileName(path) == "LobbyCharacters.cs");
        Assert.Empty(Violations(files));
    }

    [Theory]
    [InlineData("using ChogGames.Core.Collectors;")]
    [InlineData("var bonus = player.ChogsHeld * 2;")]
    [InlineData("if (CollectorTier.For(held) is not null) { }")]
    public void Holdings_access_is_reported(string line)
    {
        var violation = Assert.Single(TraitAccessRules.FindViolations("Rules.cs", line, Forbidden, "read collector holdings"));

        Assert.StartsWith("Rules.cs:1 must not read collector holdings", violation);
    }
}
