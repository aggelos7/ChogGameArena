namespace ChogGames.Api.Catalog;

/// <summary>Game catalogue bound from the <c>Games</c> configuration section (non-secret).</summary>
public sealed class GameCatalogOptions
{
    public const string SectionName = "Games";

    public List<GameCatalogEntryOptions> Items { get; set; } = [];
}

public sealed class GameCatalogEntryOptions
{
    /// <summary>Stable game type identifier, e.g. <c>chog-life</c>. Never rename once released.</summary>
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    /// <summary><c>Available</c>, <c>ComingSoon</c> or <c>Maintenance</c>.</summary>
    public string Status { get; set; } = string.Empty;
}
