namespace ChogGames.Core.Games;

/// <summary>The configured game catalogue (SUITE-001): which games are listed and whether they are enabled.</summary>
public interface IGameCatalog
{
    /// <summary>The catalogue entry for <paramref name="gameType"/>, or null when it is not listed.</summary>
    GameCatalogItem? Find(string gameType);
}
