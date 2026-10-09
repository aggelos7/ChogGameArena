namespace ChogGames.Architecture.Tests;

public class ProjectDependencyTests
{
    private static readonly string BackendRoot = FindBackendRoot();

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

    private static Dictionary<string, IReadOnlyCollection<string>> SourceProjects() =>
        Directory.GetFiles(Path.Combine(BackendRoot, "src"), "*.csproj", SearchOption.AllDirectories)
            .ToDictionary(path => Path.GetFileNameWithoutExtension(path)!, ProjectDependencyRules.ReadProjectReferences);

    [Fact]
    public void All_expected_projects_exist()
    {
        Assert.Equal(
            ProjectDependencyRules.Allowed.Keys.Order(),
            SourceProjects().Keys.Order());
    }

    [Fact]
    public void Solution_contains_every_project()
    {
        var solution = File.ReadAllText(Path.Combine(BackendRoot, "ChogGames.sln"));
        var projects = Directory.GetFiles(BackendRoot, "*.csproj", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Select(Path.GetFileNameWithoutExtension);

        Assert.All(projects, project => Assert.Contains($"\"{project}\"", solution));
    }

    [Fact]
    public void Project_references_follow_dependency_rules()
    {
        Assert.Empty(ProjectDependencyRules.FindViolations(SourceProjects()));
    }

    [Fact]
    public void Api_references_core()
    {
        Assert.Contains("ChogGames.Core", SourceProjects()["ChogGames.Api"]);
    }

    [Fact]
    public void Core_has_no_project_references()
    {
        Assert.NotEmpty(SourceProjects()["ChogGames.Core"]); // DELIBERATE CI FAILURE CHECK
    }

    [Theory]
    [InlineData("ChogGames.Core", "ChogGames.Infrastructure")]
    [InlineData("ChogGames.Core", "ChogGames.Api")]
    [InlineData("ChogGames.Games.ChogLife", "ChogGames.Infrastructure")]
    [InlineData("ChogGames.Games.ChogLife", "ChogGames.Games.ChogRace")]
    [InlineData("ChogGames.Games.ChogRace", "ChogGames.Api")]
    [InlineData("ChogGames.Infrastructure", "ChogGames.Api")]
    [InlineData("ChogGames.Infrastructure", "ChogGames.Games.ChogLife")]
    public void Forbidden_reference_is_reported(string project, string forbiddenReference)
    {
        var references = new Dictionary<string, IReadOnlyCollection<string>> { [project] = [forbiddenReference] };

        var violation = Assert.Single(ProjectDependencyRules.FindViolations(references));
        Assert.Equal($"{project} must not reference {forbiddenReference}.", violation);
    }

    [Fact]
    public void Unregistered_project_is_reported()
    {
        var references = new Dictionary<string, IReadOnlyCollection<string>> { ["ChogGames.Games.NewGame"] = ["ChogGames.Core"] };

        Assert.Contains("no dependency rule", Assert.Single(ProjectDependencyRules.FindViolations(references)));
    }
}
