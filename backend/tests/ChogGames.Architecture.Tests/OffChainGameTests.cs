using System.Text.RegularExpressions;

namespace ChogGames.Architecture.Tests;

/// <summary>
/// STORY-035, STORY-050, DEC-010, DEC-037, DEC-054: gameplay is off-chain. MON are fictional game points, so no game module touches
/// wallets, the chain, the network or crypto libraries, and no real token can ever move.
/// </summary>
public class OffChainGameTests
{
    private const string Reason = "reach wallets or the network";

    private static readonly IReadOnlyList<Regex> Forbidden =
    [
        TraitAccessRules.Token(@"ChogGames\.Core\.Wallets"),
        TraitAccessRules.Token(@"ChogGames\.Infrastructure"),
        TraitAccessRules.Token(@"System\.Net"),
        TraitAccessRules.Token("HttpClient"),
        TraitAccessRules.Token(@"Org\.BouncyCastle"),
        TraitAccessRules.Token("WalletAddress"),
    ];

    private static readonly string Src = Path.Combine(FindBackendRoot(), "src");

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

    [Fact]
    public void No_game_module_reaches_wallets_or_the_network()
    {
        var files = Directory.GetDirectories(Src, "ChogGames.Games.*")
            .SelectMany(module => Directory.GetFiles(module, "*.cs", SearchOption.AllDirectories))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .ToList();

        Assert.Contains(files, path => Path.GetFileName(path) == "LifeRules.cs");
        Assert.Contains(files, path => Path.GetFileName(path) == "RaceRules.cs");
        Assert.Empty(files.SelectMany(path =>
            TraitAccessRules.FindViolations(Path.GetRelativePath(Src, path), File.ReadAllText(path), Forbidden, Reason)));
    }

    [Theory]
    [InlineData("using ChogGames.Core.Wallets;")]
    [InlineData("using System.Net.Http;")]
    [InlineData("var client = new HttpClient();")]
    [InlineData("WalletAddress payee,")]
    public void Wallet_or_network_access_is_reported(string line)
    {
        var violation = Assert.Single(TraitAccessRules.FindViolations("Game.cs", line, Forbidden, Reason));

        Assert.StartsWith("Game.cs:1 must not reach wallets or the network", violation);
    }
}
