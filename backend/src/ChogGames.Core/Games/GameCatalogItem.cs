namespace ChogGames.Core.Games;

/// <summary>Public catalogue entry for a game, identified by its stable game type (e.g. <c>chog-life</c>).</summary>
public sealed record GameCatalogItem(string Id, string Name, string Description, GameStatus Status);
