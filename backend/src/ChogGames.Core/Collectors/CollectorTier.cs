namespace ChogGames.Core.Collectors;

/// <summary>
/// A cosmetic collector milestone (STORY-060, DEC-065): a badge and an avatar frame for holding at least
/// <see cref="MinChogs"/> Chog NFTs. Purely visual; holdings never reach a game, an ability pool or a match rule.
/// </summary>
public sealed record CollectorTier(string Id, string Name, int MinChogs)
{
    /// <summary>Milestones version 1, from the lowest.</summary>
    public static IReadOnlyList<CollectorTier> All { get; } =
    [
        new("holder", "Holder", 1),
        new("collector", "Collector", 3),
        new("patron", "Patron", 5),
        new("whale", "Whale", 10),
    ];

    /// <summary>The highest tier reached with <paramref name="chogsHeld"/> Chogs, or null below the first.</summary>
    public static CollectorTier? For(int chogsHeld) => All.LastOrDefault(t => chogsHeld >= t.MinChogs);

    /// <summary>The next tier to reach after <paramref name="chogsHeld"/> Chogs, or null at the top.</summary>
    public static CollectorTier? NextAfter(int chogsHeld) => All.FirstOrDefault(t => chogsHeld < t.MinChogs);
}
