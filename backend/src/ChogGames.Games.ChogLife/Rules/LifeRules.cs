using ChogGames.Core.Games;

namespace ChogGames.Games.ChogLife.Rules;

/// <summary>
/// A version of the Chog Life rules: the board, the starting MON, the first-finish bonus, the match's time cap and the
/// die (STORY-035, LIFE-001, LIFE-002, LIFE-007, ENG-011, DEC-037). Like an ability catalog (DEC-032) it is versioned
/// code in the game module, checked when it is built, so a malformed board stops the API at startup instead of
/// failing during a match. MON are fictional whole-number game points (DEC-010); nothing here touches a wallet or
/// the chain.
/// </summary>
public sealed class LifeRules
{
    public const int MinTiles = 30;
    public const int MaxTiles = 40;

    /// <summary>The most MON a single reward or loss tile may move.</summary>
    public const int MaxTileAmount = 1_000;

    /// <summary>The most MON a player may start with or earn as the finish bonus.</summary>
    public const int MaxStartingMon = 10_000;

    public const int MinDieFaces = 2;
    public const int MaxDieFaces = 12;

    /// <summary>The hard limit on any match's duration (ENG-011).</summary>
    public static readonly TimeSpan MaxTimeCap = TimeSpan.FromMinutes(20);

    private LifeRules(int version, IReadOnlyList<LifeTile> tiles, int startingMon, int finishBonus, TimeSpan timeCap, int dieFaces)
    {
        Version = version;
        Tiles = tiles;
        StartingMon = startingMon;
        FinishBonus = finishBonus;
        TimeCap = timeCap;
        DieFaces = dieFaces;
    }

    /// <summary>The game these rules are for.</summary>
    public string GameType => GameDefinition.ChogLife;

    /// <summary>The rules' version, from 1. Any change to the rules bumps it.</summary>
    public int Version { get; }

    /// <summary>Every tile, by index: <see cref="LifeTileType.Start"/> first and <see cref="LifeTileType.Finish"/> last.</summary>
    public IReadOnlyList<LifeTile> Tiles { get; }

    public LifeTile Start => Tiles[0];

    public LifeTile Finish => Tiles[^1];

    /// <summary>The liquid MON each player begins with (LIFE-002).</summary>
    public int StartingMon { get; }

    /// <summary>The MON only the first player to reach the finish gets (LIFE-007).</summary>
    public int FinishBonus { get; }

    /// <summary>The longest a match may last (ENG-011); enforced with STORY-041.</summary>
    public TimeSpan TimeCap { get; }

    /// <summary>A roll is one die from 1 to this many faces.</summary>
    public int DieFaces { get; }

    /// <summary>
    /// Rules with these values, or <see cref="ArgumentException"/> naming the first problem: the board must have
    /// <see cref="MinTiles"/> to <see cref="MaxTiles"/> tiles indexed in order, from one start to one finish, every
    /// link pointing forward to an existing tile, every tile reachable, each fork's safe and degen lanes joining the
    /// main track again at the same tile, and every amount and event kind on the tiles that use one.
    /// </summary>
    public static LifeRules Create(
        int version,
        IEnumerable<LifeTile> tiles,
        int startingMon,
        int finishBonus,
        TimeSpan timeCap,
        int dieFaces)
    {
        if (version < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(version), version, "A rules version starts at 1.");
        }

        if (startingMon is < 1 or > MaxStartingMon)
        {
            throw new ArgumentOutOfRangeException(nameof(startingMon), startingMon, $"Players start with 1 to {MaxStartingMon} MON.");
        }

        if (finishBonus is < 0 or > MaxStartingMon)
        {
            throw new ArgumentOutOfRangeException(nameof(finishBonus), finishBonus, $"The finish bonus is 0 to {MaxStartingMon} MON.");
        }

        if (timeCap <= TimeSpan.Zero || timeCap > MaxTimeCap)
        {
            throw new ArgumentOutOfRangeException(nameof(timeCap), timeCap, $"The time cap must be positive and at most {MaxTimeCap}.");
        }

        if (dieFaces is < MinDieFaces or > MaxDieFaces)
        {
            throw new ArgumentOutOfRangeException(nameof(dieFaces), dieFaces, $"A die has {MinDieFaces} to {MaxDieFaces} faces.");
        }

        ArgumentNullException.ThrowIfNull(tiles);
        var board = tiles.ToArray();
        CheckBoard(board);
        return new LifeRules(version, board, startingMon, finishBonus, timeCap, dieFaces);
    }

    private static void CheckBoard(LifeTile[] board)
    {
        if (board.Length is < MinTiles or > MaxTiles)
        {
            throw Invalid($"The board has {board.Length} tiles; it needs {MinTiles} to {MaxTiles}.");
        }

        if (Array.IndexOf(board, null) is var missing and >= 0)
        {
            throw Invalid($"Tile {missing} is missing.");
        }

        // Every tile on its own first, so a link check can rely on the tile it points to.
        for (var i = 0; i < board.Length; i++)
        {
            CheckTile(board, i);
        }

        foreach (var tile in board)
        {
            CheckLinks(board, tile);
        }

        // Links only point forward, so a tile entered from another is reachable from the start.
        var entered = board.SelectMany(tile => tile.Successors).ToHashSet();
        if (Enumerable.Range(1, board.Length - 1).FirstOrDefault(i => !entered.Contains(i)) is var unreachable and > 0)
        {
            throw Invalid($"Tile {unreachable} can't be reached.");
        }

        foreach (var branch in board.Where(tile => tile.Type == LifeTileType.Branch))
        {
            var safeEnd = LaneEnd(board, branch.SafeNext!.Value);
            var degenEnd = LaneEnd(board, branch.DegenNext!.Value);
            if (safeEnd != degenEnd)
            {
                throw Invalid($"The lanes of branch {branch.Index} join the main track at tiles {safeEnd} and {degenEnd}; they must join at one.");
            }
        }
    }

    private static void CheckTile(LifeTile[] board, int i)
    {
        var tile = board[i];
        if (tile.Index != i)
        {
            throw Invalid($"Tile {i} has index {tile.Index}; tiles must be listed in index order.");
        }

        if (!Enum.IsDefined(tile.Type) || !Enum.IsDefined(tile.Lane))
        {
            throw Invalid($"Tile {i} has an unknown type or lane.");
        }

        var expectedType = i == 0 ? LifeTileType.Start : i == board.Length - 1 ? LifeTileType.Finish : (LifeTileType?)null;
        if (expectedType is { } required ? tile.Type != required : tile.Type is LifeTileType.Start or LifeTileType.Finish)
        {
            throw Invalid($"Tile {i} is {tile.Type}; the board starts with the only Start and ends with the only Finish.");
        }

        if ((tile.Type is LifeTileType.Start or LifeTileType.Finish or LifeTileType.Branch) && tile.Lane != LifeLane.Main)
        {
            throw Invalid($"Tile {i} is a {tile.Type} and must be on the main track.");
        }

        var usesAmount = tile.Type is LifeTileType.Reward or LifeTileType.Loss;
        if (usesAmount ? tile.Amount is < 1 or > MaxTileAmount : tile.Amount != 0)
        {
            throw Invalid(usesAmount
                ? $"Tile {i} ({tile.Type}) needs an amount of 1 to {MaxTileAmount} MON."
                : $"Tile {i} ({tile.Type}) has no amount.");
        }

        var usesEvent = tile.Type == LifeTileType.Event;
        if (usesEvent != tile.Event.HasValue || (tile.Event is { } kind && !Enum.IsDefined(kind)))
        {
            throw Invalid(usesEvent ? $"Tile {i} is an Event tile and needs a known event kind." : $"Tile {i} ({tile.Type}) has no event.");
        }
    }

    private static void CheckLinks(LifeTile[] board, LifeTile tile)
    {
        var i = tile.Index;
        var (needsNext, needsLanes) = tile.Type switch
        {
            LifeTileType.Finish => (false, false),
            LifeTileType.Branch => (false, true),
            _ => (true, false),
        };

        if (tile.Next.HasValue != needsNext || tile.SafeNext.HasValue != needsLanes || tile.DegenNext.HasValue != needsLanes)
        {
            throw Invalid(tile.Type switch
            {
                LifeTileType.Finish => $"Tile {i} is the Finish and links to nothing.",
                LifeTileType.Branch => $"Tile {i} is a Branch and links only to its safe and degen lanes.",
                _ => $"Tile {i} ({tile.Type}) links to exactly one next tile.",
            });
        }

        foreach (var successor in tile.Successors)
        {
            if (successor <= i || successor >= board.Length)
            {
                throw Invalid($"Tile {i} links to {successor}; links must point forward to a tile on the board.");
            }
        }

        if (needsLanes)
        {
            CheckLane(board, i, tile.SafeNext!.Value, LifeLane.Safe);
            CheckLane(board, i, tile.DegenNext!.Value, LifeLane.Degen);
        }
        else if (tile.Next is { } next)
        {
            var nextLane = board[next].Lane;
            if (nextLane != LifeLane.Main && nextLane != tile.Lane)
            {
                throw Invalid($"Tile {i} ({tile.Lane}) links to tile {next} on the {nextLane} lane; only a Branch enters a lane.");
            }
        }
    }

    private static void CheckLane(LifeTile[] board, int branch, int first, LifeLane lane)
    {
        if (board[first].Lane != lane)
        {
            throw Invalid($"Branch {branch} links to tile {first} as its {lane} lane, but that tile isn't on a {lane} lane.");
        }
    }

    /// <summary>The main-track tile a lane starting at <paramref name="first"/> leads back to.</summary>
    private static int LaneEnd(LifeTile[] board, int first)
    {
        var index = first;
        while (board[index].Lane != LifeLane.Main)
        {
            index = board[index].Next!.Value;
        }

        return index;
    }

    private static ArgumentException Invalid(string message) => new(message, "tiles");
}
