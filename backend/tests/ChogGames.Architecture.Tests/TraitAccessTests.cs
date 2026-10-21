namespace ChogGames.Architecture.Tests;

/// <summary>
/// STORY-032, DEC-034: no game module reads NFT traits, and neither does the shared match and ability engine, so a
/// trait resolver can be swapped in without changing either.
/// </summary>
public class TraitAccessTests
{
    private static readonly string Src = Path.Combine(FindBackendRoot(), "src");

    /// <summary>
    /// The shared engine: Core without its NFT and player types. Only the resolver contract and the trait-based strategy
    /// with its configured resolver and rules (STORY-061, DEC-066), and the holder strategy (STORY-090, DEC-077) name traits.
    /// </summary>
    private static readonly string[] EngineFolders = ["Abilities", "Games", "Matches", "Rooms"];

    private static readonly string[] TraitAwareFiles =
        ["ITraitAbilityResolver.cs", "TraitBasedAbilityStrategy.cs", "ConfiguredTraitAbilityResolver.cs", "TraitAbilityRule.cs", "HolderAbilityStrategy.cs"];

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
        Directory.Exists(directory)
            ? Directory.GetFiles(directory, "*.cs", SearchOption.AllDirectories)
                .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                    && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            : [];

    private static IReadOnlyList<string> Violations(IEnumerable<string> files, IReadOnlyList<System.Text.RegularExpressions.Regex> rules) =>
        [.. files.SelectMany(path => TraitAccessRules.FindViolations(Path.GetRelativePath(Src, path), File.ReadAllText(path), rules))];

    [Fact]
    public void Every_game_module_is_checked()
    {
        var modules = Directory.GetDirectories(Src, "ChogGames.Games.*").Select(Path.GetFileName);

        Assert.Equal(
            ProjectDependencyRules.Allowed.Keys.Where(p => p.StartsWith("ChogGames.Games.", StringComparison.Ordinal)).Order(),
            modules.Order());
    }

    [Fact]
    public void No_game_module_reads_nft_traits()
    {
        var files = Directory.GetDirectories(Src, "ChogGames.Games.*").SelectMany(SourceFiles);

        Assert.Empty(Violations(files, TraitAccessRules.GameModuleForbidden));
    }

    [Fact]
    public void The_shared_engine_does_not_read_nft_traits()
    {
        var files = EngineFolders
            .SelectMany(folder => SourceFiles(Path.Combine(Src, "ChogGames.Core", folder)))
            .Where(path => !TraitAwareFiles.Contains(Path.GetFileName(path)))
            .ToList();

        Assert.NotEmpty(files);
        Assert.Contains(files, path => Path.GetFileName(path) == "AbilityEngine.cs");
        Assert.Empty(Violations(files, TraitAccessRules.EngineForbidden));
    }

    [Theory]
    [InlineData("using ChogGames.Core.Nfts;")]
    [InlineData("var rare = member.Character.Traits.Any(t => t.Value == \"Legendary\");")]
    [InlineData("CharacterSnapshot character = context.Character;")]
    [InlineData("foreach (NftTrait trait in traits) { }")]
    [InlineData("var set = TraitSet.From(snapshot);")]
    [InlineData("ITraitAbilityResolver resolver,")]
    public void Trait_access_in_a_game_module_is_reported(string line)
    {
        var violation = Assert.Single(TraitAccessRules.FindViolations("Game.cs", $"namespace X;\n{line}\n", TraitAccessRules.GameModuleForbidden));

        Assert.StartsWith("Game.cs:2 must not read NFT traits", violation);
    }

    [Theory]
    [InlineData("var traits = snapshot.Traits;")]
    [InlineData("TraitSet traits = TraitSet.Empty;")]
    [InlineData("new NftTrait(\"Rarity\", \"Rare\")")]
    public void Trait_access_in_the_engine_is_reported(string line)
    {
        Assert.NotEmpty(TraitAccessRules.FindViolations("Engine.cs", line, TraitAccessRules.EngineForbidden));
    }

    [Theory]
    [InlineData("/// Handlers never see NFT traits or a <see cref=\"CharacterSnapshot\"/>.")]
    [InlineData("// TraitSet is for strategies only.")]
    [InlineData("var traitsSeen = 0; var MyTraitSetting = 1;")]
    [InlineData("var attributes = state.Attributes;")]
    public void Comments_and_similar_names_are_not_reported(string line)
    {
        Assert.Empty(TraitAccessRules.FindViolations("Game.cs", line, TraitAccessRules.GameModuleForbidden));
    }
}
