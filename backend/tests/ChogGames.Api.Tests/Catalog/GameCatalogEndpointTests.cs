using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace ChogGames.Api.Tests.Catalog;

public class GameCatalogEndpointTests
{
    private sealed record GameDto(string Id, string Name, string Description, string Status);

    private static async Task<GameDto[]> GetGamesAsync(IReadOnlyDictionary<string, string?>? settings = null)
    {
        await using var factory = new ApiFactory(ApiFactory.UnusedDatabase, settings);
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/api/games");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<GameDto[]>(new JsonSerializerOptions(JsonSerializerDefaults.Web)))!;
    }

    [Fact]
    public async Task Default_catalogue_lists_every_game_as_coming_soon()
    {
        var games = await GetGamesAsync();

        Assert.Equal(["chog-life", "chog-race", "chog-royale"], games.Select(g => g.Id));
        Assert.Equal(["Chog Life", "Chog Race", "Chog Royale"], games.Select(g => g.Name));
        Assert.All(games, game => Assert.Equal("ComingSoon", game.Status));
        Assert.All(games, game => Assert.False(string.IsNullOrWhiteSpace(game.Description)));
    }

    [Fact]
    public async Task Statuses_come_from_configuration_in_configured_order()
    {
        var games = await GetGamesAsync(new Dictionary<string, string?>
        {
            ["Games:Items:0:Status"] = "Available",
            ["Games:Items:1:Status"] = "Maintenance",
            ["Games:Items:3:Id"] = "chog-future",
            ["Games:Items:3:Name"] = "Chog Future",
            ["Games:Items:3:Status"] = "ComingSoon",
        });

        Assert.Equal(
            [("chog-life", "Available"), ("chog-race", "Maintenance"), ("chog-royale", "ComingSoon"), ("chog-future", "ComingSoon")],
            games.Select(g => (g.Id, g.Status)));
    }

    [Fact]
    public async Task Status_is_serialized_as_a_name_not_a_number()
    {
        await using var factory = new ApiFactory(ApiFactory.UnusedDatabase);
        using var client = factory.CreateClient();

        var json = await client.GetStringAsync("/api/games");

        Assert.Contains("\"status\":\"ComingSoon\"", json);
    }

    [Theory]
    [InlineData("Games:Items:1:Status", "Broken", "'Games:Items:1:Status' must be one of")]
    [InlineData("Games:Items:1:Id", "chog-life", "duplicates game id 'chog-life'")]
    [InlineData("Games:Items:0:Id", "Chog Life", "must be lowercase kebab-case")]
    [InlineData("Games:Items:0:Name", "", "'Games:Items:0:Name' is required")]
    public async Task Invalid_catalogue_configuration_stops_startup(string key, string value, string expectedMessage)
    {
        await using var factory = new ApiFactory(ApiFactory.UnusedDatabase, new Dictionary<string, string?> { [key] = value });

        var error = Assert.Throws<OptionsValidationException>(() => factory.CreateClient());
        Assert.Contains(expectedMessage, error.Message);
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    public async Task Catalogue_is_read_only(string method)
    {
        await using var factory = new ApiFactory(ApiFactory.UnusedDatabase);
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), "/api/games"));

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }
}
