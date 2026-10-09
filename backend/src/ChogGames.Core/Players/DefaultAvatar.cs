namespace ChogGames.Core.Players;

/// <summary>
/// Built-in placeholder avatar available to every player (SUITE-002/SUITE-004). Rendered by the
/// client from its colour until official Chog artwork is approved (DEC-005).
/// </summary>
public sealed record DefaultAvatar(string Id, string Name, string Color)
{
    public const int MaxIdLength = 32;

    /// <summary>Assigned to new players.</summary>
    public static DefaultAvatar Classic { get; } = new("classic", "Classic", "#5b2bd6");

    public static IReadOnlyList<DefaultAvatar> All { get; } =
    [
        Classic,
        new("mint", "Mint", "#1a7f5a"),
        new("sunset", "Sunset", "#c2410c"),
        new("ocean", "Ocean", "#1d4ed8"),
        new("berry", "Berry", "#be185d"),
        new("gold", "Gold", "#a16207"),
    ];

    public static bool TryFind(string? id, out DefaultAvatar avatar)
    {
        avatar = All.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.Ordinal))!;
        return avatar is not null;
    }
}
