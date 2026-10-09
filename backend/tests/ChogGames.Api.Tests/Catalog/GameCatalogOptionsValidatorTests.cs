using ChogGames.Api.Catalog;

namespace ChogGames.Api.Tests.Catalog;

public class GameCatalogOptionsValidatorTests
{
    private static GameCatalogEntryOptions Game(string id = "chog-life", string name = "Chog Life", string status = "Available") =>
        new() { Id = id, Name = name, Status = status };

    private static IEnumerable<string> Errors(params GameCatalogEntryOptions[] items) =>
        new GameCatalogOptionsValidator().Validate(null, new GameCatalogOptions { Items = [.. items] }).Failures ?? [];

    [Fact]
    public void Valid_catalogue_passes()
    {
        Assert.Empty(Errors(Game(), Game("chog-race", "Chog Race", "ComingSoon"), Game("x-1", "X", "Maintenance")));
    }

    [Fact]
    public void Empty_catalogue_fails()
    {
        Assert.Contains("must list at least one game", Assert.Single(Errors()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Chog-Life")]
    [InlineData("chog_life")]
    [InlineData("-chog")]
    [InlineData("chog--life")]
    public void Invalid_id_fails(string id)
    {
        Assert.Contains("must be lowercase kebab-case", Assert.Single(Errors(Game(id))));
    }

    [Theory]
    [InlineData("")]
    [InlineData("available")]
    [InlineData("1")]
    [InlineData("Disabled")]
    public void Unknown_status_fails(string status)
    {
        Assert.Contains("must be one of Available, ComingSoon, Maintenance", Assert.Single(Errors(Game(status: status))));
    }

    [Fact]
    public void All_errors_are_reported_together()
    {
        Assert.Equal(3, Errors(Game("Bad Id", " ", "Nope")).Count());
    }
}
