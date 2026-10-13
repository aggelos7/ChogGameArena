namespace ChogGames.Games.ChogLife.Rules;

/// <summary>
/// One space of the Chog Life board (LIFE-001, DEC-037). Tiles link forward by index: a branch to the first tile of
/// its safe and degen lanes, the finish to nothing, every other tile to exactly one <see cref="Next"/>. Use the
/// factory methods; <see cref="LifeRules.Create"/> checks every tile.
/// </summary>
public sealed record LifeTile(
    int Index,
    LifeTileType Type,
    LifeLane Lane,
    int? Next,
    int? SafeNext = null,
    int? DegenNext = null,
    int Amount = 0,
    LifeEventKind? Event = null)
{
    public static LifeTile Start(int index, int next) => new(index, LifeTileType.Start, LifeLane.Main, next);

    public static LifeTile Normal(int index, LifeLane lane, int next) => new(index, LifeTileType.Normal, lane, next);

    /// <summary>A tile that pays <paramref name="amount"/> MON.</summary>
    public static LifeTile Reward(int index, LifeLane lane, int next, int amount) =>
        new(index, LifeTileType.Reward, lane, next, Amount: amount);

    /// <summary>A tile that costs <paramref name="amount"/> MON.</summary>
    public static LifeTile Loss(int index, LifeLane lane, int next, int amount) =>
        new(index, LifeTileType.Loss, lane, next, Amount: amount);

    public static LifeTile EventTile(int index, LifeLane lane, int next, LifeEventKind kind) =>
        new(index, LifeTileType.Event, lane, next, Event: kind);

    public static LifeTile Branch(int index, int safeNext, int degenNext) =>
        new(index, LifeTileType.Branch, LifeLane.Main, null, safeNext, degenNext);

    public static LifeTile Finish(int index) => new(index, LifeTileType.Finish, LifeLane.Main, null);

    /// <summary>The tiles a player can move on to from here: none from the finish, two from a branch.</summary>
    public IEnumerable<int> Successors =>
        new[] { Next, SafeNext, DegenNext }.Where(link => link.HasValue).Select(link => link!.Value);
}
