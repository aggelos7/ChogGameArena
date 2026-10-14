using ChogGames.Core.Games;
using ChogGames.Core.Matches;

namespace ChogGames.Games.ChogLife.Rules;

/// <summary>
/// A version of the Chog Life rules: the board, the starting MON, the first-finish bonus, the match's time cap, the
/// die (STORY-035, LIFE-001, LIFE-002, LIFE-007, ENG-011, DEC-037), what each event does (STORY-038, DEC-040) and the
/// lane a player who doesn't choose at a fork takes (STORY-039, DEC-041) and what MON staked on a fork's lane returns
/// (STORY-040, DEC-042). Like an ability catalog (DEC-032) it is versioned
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

    /// <summary>The most liquid MON a player can hold; gains above it are lost, so a balance never overflows.</summary>
    public const int MaxMon = 1_000_000;

    /// <summary>The most a stake may pay back, in percent of the stake (10x).</summary>
    public const int MaxStakePercent = 1_000;

    /// <summary>The hard limit on any match's duration (ENG-011).</summary>
    public static readonly TimeSpan MaxTimeCap = TimeSpan.FromMinutes(20);

    private LifeRules(
        int version,
        IReadOnlyList<LifeTile> tiles,
        int startingMon,
        int finishBonus,
        TimeSpan timeCap,
        int dieFaces,
        IReadOnlyDictionary<LifeEventKind, LifeEvent> events,
        LifeLane defaultLane,
        int forkStake,
        IReadOnlyDictionary<LifeLane, LifeLaneStake> stakes)
    {
        Version = version;
        Tiles = tiles;
        StartingMon = startingMon;
        FinishBonus = finishBonus;
        TimeCap = timeCap;
        DieFaces = dieFaces;
        Events = events;
        DefaultLane = defaultLane;
        ForkStake = forkStake;
        Stakes = stakes;
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

    /// <summary>What an event tile of each kind does; every kind on the board has one.</summary>
    public IReadOnlyDictionary<LifeEventKind, LifeEvent> Events { get; }

    /// <summary>
    /// The lane taken at a fork when the player's turn runs out before they choose, or when a pawn from before STORY-039
    /// stands on a branch with no lane (LIFE-005, DEC-041): <see cref="LifeLane.Safe"/> or <see cref="LifeLane.Degen"/>.
    /// </summary>
    public LifeLane DefaultLane { get; }

    /// <summary>
    /// The MON a player stakes on the lane they take at a fork, or all their liquid MON if they hold less (LIFE-005,
    /// STORY-040, DEC-042); 0 when this version has no stakes.
    /// </summary>
    public int ForkStake { get; }

    /// <summary>What a stake on each lane returns, Safe and Degen; empty when <see cref="ForkStake"/> is 0.</summary>
    public IReadOnlyDictionary<LifeLane, LifeLaneStake> Stakes { get; }

    /// <summary>
    /// Rules with these values, or <see cref="ArgumentException"/> naming the first problem: the board must have
    /// <see cref="MinTiles"/> to <see cref="MaxTiles"/> tiles indexed in order, from one start to one finish, every
    /// link pointing forward to an existing tile, every tile reachable, each fork's safe and degen lanes joining the
    /// main track again at the same tile, and every amount and event kind on the tiles that use one. Each event kind is
    /// defined once, every kind on the board is defined, and each event's outcomes have chances of 1–100% adding up to
    /// 100% and move some MON within range. The default lane is Safe or Degen. A fork stake of 1 to
    /// <see cref="MaxTileAmount"/> MON needs the returns of the Safe and Degen lanes, each defined once, with chances of
    /// 1–100% adding up to 100% and paying back 0 to <see cref="MaxStakePercent"/>% of the stake.
    /// </summary>
    public static LifeRules Create(
        int version,
        IEnumerable<LifeTile> tiles,
        int startingMon,
        int finishBonus,
        TimeSpan timeCap,
        int dieFaces,
        IEnumerable<LifeEvent> events,
        LifeLane defaultLane = LifeLane.Safe,
        int forkStake = 0,
        IEnumerable<LifeLaneStake>? stakes = null)
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

        if (defaultLane is not (LifeLane.Safe or LifeLane.Degen))
        {
            throw new ArgumentOutOfRangeException(nameof(defaultLane), defaultLane, "The default lane is Safe or Degen.");
        }

        ArgumentNullException.ThrowIfNull(tiles);
        var board = tiles.ToArray();
        CheckBoard(board);
        ArgumentNullException.ThrowIfNull(events);
        var byKind = CheckEvents(board, events.ToArray());
        var byLane = CheckStakes(forkStake, stakes?.ToArray());
        return new LifeRules(version, board, startingMon, finishBonus, timeCap, dieFaces, byKind, defaultLane, forkStake, byLane);
    }

    /// <summary>
    /// The tiles a pawn on <paramref name="from"/> passes for a roll of <paramref name="steps"/>, the last being where it
    /// stops (STORY-037, DEC-039). It stops early on a Branch, whatever steps are left, and on the Finish, which a roll
    /// may overshoot. A pawn leaving a branch takes the lane its player chose there (STORY-039, DEC-041); with no
    /// <paramref name="lane"/> it takes <see cref="DefaultLane"/>. The lane only matters when <paramref name="from"/> is a
    /// branch. A pawn on the Finish doesn't move, so the path is empty.
    /// </summary>
    public IReadOnlyList<int> Path(int from, int steps, LifeLane? lane = null)
    {
        if (from < 0 || from >= Tiles.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(from), from, "The pawn isn't on the board.");
        }

        if (steps < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(steps), steps, "A move has at least one step.");
        }

        var exit = lane ?? DefaultLane;
        if (exit is not (LifeLane.Safe or LifeLane.Degen))
        {
            throw new ArgumentOutOfRangeException(nameof(lane), lane, "A pawn leaves a branch by its Safe or Degen lane.");
        }

        var path = new List<int>();
        var tile = Tiles[from];
        while (path.Count < steps && NextFrom(tile, exit) is { } next)
        {
            tile = Tiles[next];
            path.Add(next);
            if (tile.Type is LifeTileType.Branch or LifeTileType.Finish)
            {
                break;
            }
        }

        return path;
    }

    private static int? NextFrom(LifeTile tile, LifeLane lane) => tile.Type != LifeTileType.Branch
        ? tile.Next
        : lane == LifeLane.Degen ? tile.DegenNext : tile.SafeNext;

    /// <summary>
    /// What landing on <paramref name="tile"/> does to a player holding <paramref name="liquid"/> MON (STORY-038,
    /// DEC-040): the signed MON it moves, or null for a tile that does nothing. Reward and Loss tiles move their amount;
    /// an event tile draws one of its outcomes from <paramref name="random"/>. A loss may be bigger than the liquid MON
    /// held: <see cref="LifePortfolio.Settle"/> turns the rest into a liability (STORY-040, DEC-042).
    /// </summary>
    public int? Resolve(int tile, int liquid, IRandomSource random)
    {
        ArgumentNullException.ThrowIfNull(random);
        if (liquid is < 0 or > MaxMon)
        {
            throw new ArgumentOutOfRangeException(nameof(liquid), liquid, $"Liquid MON is 0 to {MaxMon}.");
        }

        var landed = Tiles[tile];
        return landed.Type switch
        {
            LifeTileType.Reward => landed.Amount,
            LifeTileType.Loss => -landed.Amount,
            LifeTileType.Event => Events[landed.Event!.Value].Draw(random).Change(liquid),
            _ => null,
        };
    }

    /// <summary>
    /// What a stake of <paramref name="asset"/> pays back when the pawn rejoins the main track (STORY-040, DEC-042): one
    /// of its lane's outcomes, drawn from <paramref name="random"/> unless there is only one.
    /// </summary>
    public int Payout(LifeAsset asset, IRandomSource random)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentNullException.ThrowIfNull(random);
        if (!Stakes.TryGetValue(asset.Lane, out var stake))
        {
            throw new ArgumentOutOfRangeException(nameof(asset), asset.Lane, "These rules have no stake on that lane.");
        }

        if (asset.Amount is < 1 or > MaxMon)
        {
            throw new ArgumentOutOfRangeException(nameof(asset), asset.Amount, $"A stake is 1 to {MaxMon} MON.");
        }

        return stake.Draw(random).Payout(asset.Amount);
    }

    private static Dictionary<LifeLane, LifeLaneStake> CheckStakes(int forkStake, LifeLaneStake[]? stakes)
    {
        if (forkStake is < 0 or > MaxTileAmount)
        {
            throw new ArgumentOutOfRangeException(nameof(forkStake), forkStake, $"The fork stake is 0 to {MaxTileAmount} MON.");
        }

        var byLane = new Dictionary<LifeLane, LifeLaneStake>();
        if (forkStake == 0 && stakes is null)
        {
            return byLane;
        }

        if (stakes is null)
        {
            throw InvalidStake("A fork stake needs the returns of the Safe and Degen lanes.");
        }

        foreach (var stake in stakes)
        {
            if (stake is null || stake.Lane is not (LifeLane.Safe or LifeLane.Degen))
            {
                throw InvalidStake("A stake's returns are for the Safe or the Degen lane.");
            }

            if (!byLane.TryAdd(stake.Lane, stake))
            {
                throw InvalidStake($"The {stake.Lane} lane's returns are defined twice.");
            }

            if (stake.Outcomes is not { Count: > 0 } outcomes || outcomes.Contains(null!))
            {
                throw InvalidStake($"The {stake.Lane} lane's stake needs its outcomes.");
            }

            if (outcomes.Any(outcome => outcome.Chance is < 1 or > 100 || outcome.Percent is < 0 or > MaxStakePercent))
            {
                throw InvalidStake($"Each {stake.Lane} outcome needs a chance of 1 to 100% and pays back 0 to {MaxStakePercent}% of the stake.");
            }

            if (outcomes.Sum(outcome => outcome.Chance) != 100)
            {
                throw InvalidStake($"The chances of the {stake.Lane} lane's outcomes must add up to 100%.");
            }
        }

        if (byLane.Count != 2)
        {
            throw InvalidStake("A fork stake needs the returns of both the Safe and the Degen lane.");
        }

        return byLane;
    }

    private static Dictionary<LifeEventKind, LifeEvent> CheckEvents(LifeTile[] board, LifeEvent[] events)
    {
        var byKind = new Dictionary<LifeEventKind, LifeEvent>();
        foreach (var definition in events)
        {
            if (definition is null || !Enum.IsDefined(definition.Kind))
            {
                throw InvalidEvent("An event is missing or of an unknown kind.");
            }

            if (!byKind.TryAdd(definition.Kind, definition))
            {
                throw InvalidEvent($"The {definition.Kind} event is defined twice.");
            }

            CheckOutcomes(definition);
        }

        if (board.FirstOrDefault(tile => tile.Event is { } kind && !byKind.ContainsKey(kind)) is { } undefined)
        {
            throw InvalidEvent($"Tile {undefined.Index} is a {undefined.Event} event, which isn't defined.");
        }

        return byKind;
    }

    private static void CheckOutcomes(LifeEvent definition)
    {
        var kind = definition.Kind;
        if (definition.Outcomes is not { Count: > 0 } outcomes || outcomes.Contains(null!))
        {
            throw InvalidEvent($"The {kind} event needs its outcomes.");
        }

        foreach (var outcome in outcomes)
        {
            if (outcome.Chance is < 1 or > 100 || !Enum.IsDefined(outcome.Effect))
            {
                throw InvalidEvent($"Each {kind} outcome needs a chance of 1 to 100% and a known effect.");
            }

            if (outcome.Amount is < 0 or > MaxTileAmount || outcome.Minimum is < 0 or > MaxTileAmount || outcome.Percent is < 0 or > 100)
            {
                throw InvalidEvent($"Each {kind} outcome moves 0 to {MaxTileAmount} MON plus 0 to 100% of liquid MON, with a minimum of 0 to {MaxTileAmount}.");
            }

            if (outcome is { Amount: 0, Percent: 0, Minimum: 0 })
            {
                throw InvalidEvent($"Each {kind} outcome must move some MON.");
            }
        }

        if (outcomes.Sum(outcome => outcome.Chance) != 100)
        {
            throw InvalidEvent($"The chances of the {kind} event's outcomes must add up to 100%.");
        }
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

    private static ArgumentException InvalidEvent(string message) => new(message, "events");

    private static ArgumentException InvalidStake(string message) => new(message, "stakes");
}
