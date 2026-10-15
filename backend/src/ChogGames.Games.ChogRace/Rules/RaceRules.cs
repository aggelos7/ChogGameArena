using ChogGames.Core.Games;
using ChogGames.Core.Rooms;

namespace ChogGames.Games.ChogRace.Rules;

/// <summary>
/// A version of the Chog Race rules: the track, how far a pawn moves in a turn and the match's time cap (STORY-050,
/// RACE-001, RACE-002, RACE-009, DEC-054). Like Chog Life's rules (DEC-037) it is versioned code in the game module,
/// checked when it is built, so a track whose finish can't be reached stops the API at startup instead of trapping
/// pawns during a match. Nothing here touches a wallet or the chain (DEC-010).
/// </summary>
public sealed class RaceRules
{
    public const int MinTiles = 24;
    public const int MaxTiles = 30;

    /// <summary>The most spaces any rules version may let a roll move.</summary>
    public const int MoveLimit = 6;

    /// <summary>The furthest a Boost or Setback tile may send a pawn, either way.</summary>
    public const int MaxJump = 6;

    /// <summary>The hard limit on any match's duration (ENG-011).</summary>
    public static readonly TimeSpan MaxTimeCap = GameRoom.MaxMatchDuration;

    private RaceRules(int version, IReadOnlyList<RaceTile> tiles, int minMove, int maxMove, TimeSpan timeCap)
    {
        Version = version;
        Tiles = tiles;
        MinMove = minMove;
        MaxMove = maxMove;
        TimeCap = timeCap;
    }

    /// <summary>The game these rules are for.</summary>
    public string GameType => GameDefinition.ChogRace;

    /// <summary>The rules' version, from 1. Any change to the rules bumps it.</summary>
    public int Version { get; }

    /// <summary>Every tile, by index: <see cref="RaceTileType.Start"/> first and <see cref="RaceTileType.Finish"/> last.</summary>
    public IReadOnlyList<RaceTile> Tiles { get; }

    public RaceTile Start => Tiles[0];

    public RaceTile Finish => Tiles[^1];

    /// <summary>The fewest spaces a roll moves (RACE-002).</summary>
    public int MinMove { get; }

    /// <summary>The most spaces a roll moves (RACE-002); every value from <see cref="MinMove"/> is equally likely.</summary>
    public int MaxMove { get; }

    /// <summary>The longest a match may last (ENG-011, RACE-009); the match ends when it's up (STORY-056).</summary>
    public TimeSpan TimeCap { get; }

    /// <summary>
    /// Rules with these values, or <see cref="ArgumentException"/> naming the first problem: moves of 1 to
    /// <see cref="MoveLimit"/> spaces, a time cap of at most <see cref="MaxTimeCap"/>, and a track of <see cref="MinTiles"/>
    /// to <see cref="MaxTiles"/> tiles in index order, from one start to one finish. A Boost jumps forward and a Setback
    /// back, at most <see cref="MaxJump"/> tiles, to a tile on the track that doesn't jump again. The finish must be
    /// reachable: a pawn that can stand on a tile must have some sequence of rolls from it to the finish, so no group of
    /// setbacks can trap it.
    /// </summary>
    public static RaceRules Create(int version, IEnumerable<RaceTile> tiles, int minMove, int maxMove, TimeSpan timeCap)
    {
        if (version < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(version), version, "A rules version starts at 1.");
        }

        if (minMove < 1 || maxMove < minMove || maxMove > MoveLimit)
        {
            throw new ArgumentOutOfRangeException(nameof(maxMove), (minMove, maxMove), $"A move is 1 to {MoveLimit} spaces, the fewest no more than the most.");
        }

        if (timeCap <= TimeSpan.Zero || timeCap > MaxTimeCap)
        {
            throw new ArgumentOutOfRangeException(nameof(timeCap), timeCap, $"The time cap must be positive and at most {MaxTimeCap}.");
        }

        ArgumentNullException.ThrowIfNull(tiles);
        var track = tiles.ToArray();
        CheckTrack(track);
        CheckFinishReachable(track, minMove, maxMove);
        return new RaceRules(version, track, minMove, maxMove, timeCap);
    }

    private static void CheckTrack(RaceTile[] track)
    {
        if (track.Length is < MinTiles or > MaxTiles)
        {
            throw Invalid($"The track has {track.Length} tiles; it needs {MinTiles} to {MaxTiles}.");
        }

        if (Array.IndexOf(track, null) is var missing and >= 0)
        {
            throw Invalid($"Tile {missing} is missing.");
        }

        for (var i = 0; i < track.Length; i++)
        {
            var tile = track[i];
            if (tile.Index != i)
            {
                throw Invalid($"Tile {i} has index {tile.Index}; tiles must be listed in index order.");
            }

            if (!Enum.IsDefined(tile.Type))
            {
                throw Invalid($"Tile {i} has an unknown type.");
            }

            var expectedType = i == 0 ? RaceTileType.Start : i == track.Length - 1 ? RaceTileType.Finish : (RaceTileType?)null;
            if (expectedType is { } required ? tile.Type != required : tile.Type is RaceTileType.Start or RaceTileType.Finish)
            {
                throw Invalid($"Tile {i} is {tile.Type}; the track starts with the only Start and ends with the only Finish.");
            }
        }

        foreach (var tile in track)
        {
            CheckJump(track, tile);
        }
    }

    private static void CheckJump(RaceTile[] track, RaceTile tile)
    {
        var i = tile.Index;
        if (!tile.Jumps)
        {
            if (tile.To is not null)
            {
                throw Invalid($"Tile {i} ({tile.Type}) sends a pawn nowhere.");
            }

            return;
        }

        if (tile.To is not { } to || to < 0 || to >= track.Length)
        {
            throw Invalid($"Tile {i} ({tile.Type}) must send a pawn to a tile on the track.");
        }

        var forward = tile.Type == RaceTileType.Boost;
        if (forward ? to <= i : to >= i)
        {
            throw Invalid($"Tile {i} is a {tile.Type} to tile {to}; a Boost jumps forward and a Setback back.");
        }

        if (Math.Abs(to - i) > MaxJump)
        {
            throw Invalid($"Tile {i} sends a pawn {Math.Abs(to - i)} tiles; a jump is at most {MaxJump}.");
        }

        if (track[to].Jumps)
        {
            throw Invalid($"Tile {i} sends a pawn to tile {to}, which jumps again; a jump lands on a tile that doesn't.");
        }
    }

    /// <summary>
    /// Every tile a pawn can stand on, from the start with any rolls, must have some sequence of rolls to the finish. A
    /// roll past the finish stops on it (RACE-008), and landing on a Boost or Setback moves the pawn on to its target.
    /// </summary>
    private static void CheckFinishReachable(RaceTile[] track, int minMove, int maxMove)
    {
        var last = track.Length - 1;
        IEnumerable<int> Landings(int from) => Enumerable.Range(minMove, maxMove - minMove + 1)
            .Select(steps => Math.Min(from + steps, last))
            .Select(to => track[to].To ?? to);

        // Forward: where a pawn can stand.
        var standing = new HashSet<int> { 0 };
        var queue = new Queue<int>([0]);
        while (queue.TryDequeue(out var from))
        {
            foreach (var to in Landings(from).Where(standing.Add))
            {
                queue.Enqueue(to);
            }
        }

        // Backward: from where the finish can be reached, until nothing changes.
        var finishing = new HashSet<int> { last };
        bool grew;
        do
        {
            grew = false;
            foreach (var tile in standing.Where(tile => !finishing.Contains(tile) && Landings(tile).Any(finishing.Contains)).ToList())
            {
                grew |= finishing.Add(tile);
            }
        }
        while (grew);

        if (standing.Where(tile => !finishing.Contains(tile)).Order().FirstOrDefault(-1) is var trapped and >= 0)
        {
            throw Invalid($"The finish can't be reached from tile {trapped} with moves of {minMove} to {maxMove}.");
        }
    }

    private static ArgumentException Invalid(string message) => new(message, "tiles");
}
