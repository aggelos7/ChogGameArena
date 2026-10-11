using ChogGames.Core.Games;
using Microsoft.Extensions.Options;

namespace ChogGames.Api.Catalog;

/// <summary>The game catalogue from the <c>Games</c> configuration section, read on every call so status changes apply.</summary>
public sealed class ConfiguredGameCatalog(IOptionsMonitor<GameCatalogOptions> options) : IGameCatalog
{
    public GameCatalogItem? Find(string gameType) =>
        options.CurrentValue.Items
            .Where(entry => string.Equals(entry.Id, gameType, StringComparison.Ordinal))
            .Select(GameCatalogEndpoints.ToCatalogItem)
            .FirstOrDefault();
}
