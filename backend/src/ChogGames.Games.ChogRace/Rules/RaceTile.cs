namespace ChogGames.Games.ChogRace.Rules;

/// <summary>
/// One space of the Chog Race track (RACE-001, DEC-054). The track runs by index from the start to the finish; a Boost
/// or Setback tile sends a pawn that lands on it to <see cref="To"/>. Use the factory methods;
/// <see cref="RaceRules.Create"/> checks every tile.
/// </summary>
public sealed record RaceTile(int Index, RaceTileType Type, int? To = null)
{
    public static RaceTile Start(int index) => new(index, RaceTileType.Start);

    public static RaceTile Normal(int index) => new(index, RaceTileType.Normal);

    public static RaceTile Card(int index) => new(index, RaceTileType.Card);

    /// <summary>A tile that jumps a pawn forward to tile <paramref name="to"/>.</summary>
    public static RaceTile Boost(int index, int to) => new(index, RaceTileType.Boost, to);

    /// <summary>A tile that sends a pawn back to tile <paramref name="to"/>.</summary>
    public static RaceTile Setback(int index, int to) => new(index, RaceTileType.Setback, to);

    public static RaceTile Finish(int index) => new(index, RaceTileType.Finish);

    /// <summary>Whether landing here moves the pawn on to another tile.</summary>
    public bool Jumps => Type is RaceTileType.Boost or RaceTileType.Setback;
}
