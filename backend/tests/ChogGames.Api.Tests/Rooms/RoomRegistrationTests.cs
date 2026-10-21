using ChogGames.Core.Games;
using ChogGames.Infrastructure.Persistence;
using ChogGames.Infrastructure.Rooms;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;

namespace ChogGames.Api.Tests.Rooms;

public class RoomRegistrationTests
{
    [Fact]
    public async Task Every_registered_game_is_in_the_default_catalogue_and_vice_versa()
    {
        await using var factory = new ApiFactory(ApiFactory.UnusedDatabase);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChogGamesDbContext>();
        var catalog = scope.ServiceProvider.GetRequiredService<IGameCatalog>();

        var seeded = db.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(GameDefinition))!.GetSeedData()
            .Select(row => (string)row[nameof(GameDefinition.Id)]!)
            .ToList();

        Assert.Equal([GameDefinition.ChogLife, GameDefinition.ChogRace, GameDefinition.ChogRoyale], seeded.Order());
        Assert.All(seeded, id => Assert.NotNull(catalog.Find(id)));
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<RoomService>());
    }

    [Fact]
    public async Task Catalogue_status_comes_from_configuration_and_ids_match_exactly()
    {
        await using var factory = new ApiFactory(ApiFactory.UnusedDatabase, new Dictionary<string, string?>
        {
            ["Games:Items:0:Status"] = "Available",
            ["Games:Items:1:Status"] = "Maintenance",
        });
        var catalog = factory.Services.GetRequiredService<IGameCatalog>();

        Assert.Equal(GameStatus.Available, catalog.Find(GameDefinition.ChogLife)!.Status);
        Assert.Equal(GameStatus.Maintenance, catalog.Find(GameDefinition.ChogRace)!.Status);
        Assert.Null(catalog.Find("CHOG-LIFE"));
        Assert.Null(catalog.Find("chog-unknown"));
    }
}
