using System.Text.Json;
using System.Text.Json.Serialization;
using ChogGames.Core.Games;
using ChogGames.Core.Matches;
using ChogGames.Core.Rooms;
using ChogGames.Games.ChogLife.Rules;

namespace ChogGames.Games.ChogLife;

/// <summary>
/// Chog Life in the shared match engine (STORY-036, DEC-038). The match starts with every seated player's pawn on the
/// start tile, under the current rules version, which the state records so the match keeps its board after a new version
/// ships (DEC-037). Each player sees the board of that version and the pawns of the players still playing; the turn and
/// seats come with the room snapshot. On their turn a player sends <c>ROLL</c> (STORY-037, DEC-039): the server rolls the
/// die, moves the pawn along the board and ends the turn; the state keeps that last move, so every player sees the same
/// roll and destination. The tile the pawn stops on then resolves (STORY-038, DEC-040): Reward and Loss tiles move
/// their MON and an event tile draws one of its outcomes, and every player sees each player's liquid MON. A turn that
/// runs out is skipped.
/// </summary>
public sealed class ChogLifeMatchRules : IMatchRules
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Roll the die, move and resolve the tile: the whole turn until branch choices arrive (STORY-039).</summary>
    public const string Roll = "ROLL";

    private readonly Dictionary<int, LifeRules> _rulesByVersion;
    private readonly LifeRules _current;

    /// <param name="versions">Every rules version a match may still be playing; new matches use the highest.</param>
    public ChogLifeMatchRules(IEnumerable<LifeRules> versions)
    {
        ArgumentNullException.ThrowIfNull(versions);
        _rulesByVersion = versions.ToDictionary(rules => rules.Version);
        _current = _rulesByVersion.Count > 0
            ? _rulesByVersion[_rulesByVersion.Keys.Max()]
            : throw new ArgumentException("Chog Life needs at least one rules version.", nameof(versions));
    }

    public string GameType => GameDefinition.ChogLife;

    public JsonElement? StartState(MatchStartContext context) =>
        JsonSerializer.SerializeToElement(StartOf(context.Match, _current), Json);

    private static LifeMatchState StartOf(GameRoom match, LifeRules rules)
    {
        var pawns = match.Members
            .Where(member => member.Seat is not null)
            .OrderBy(member => member.Seat)
            .Select(member => new LifePawn(member.Seat!.Value, rules.Start.Index, rules.StartingMon))
            .ToArray();
        return new LifeMatchState(rules.Version, pawns);
    }

    /// <summary>
    /// <c>ROLL</c>: one die from the server's source of chance, never from the payload, which is ignored (DEC-031). The
    /// pawn moves along <see cref="LifeRules.Path"/>, the tile it stops on resolves with <see cref="LifeRules.Resolve"/>
    /// (an event's draw comes after the die's) and the turn ends. A pawn on the Finish doesn't move and draws nothing;
    /// its roll just passes the turn until STORY-041 decides what finished players do. A match started before its state
    /// existed (before STORY-036) gets its start state first, and pawns from before STORY-038 hold the starting MON.
    /// </summary>
    public MatchRuleDecision Apply(MatchCommandContext context)
    {
        if (context.Type != Roll)
        {
            return MatchRuleDecision.Unknown;
        }

        var stored = context.State is { } json ? Read(json) : StartOf(context.Match, _current);
        if (stored is null || !_rulesByVersion.TryGetValue(stored.RulesVersion, out var rules))
        {
            return MatchRuleDecision.Reject("game_state_unavailable");
        }

        var state = WithMon(stored, rules);
        var seat = context.Match.Members.SingleOrDefault(member => member.PlayerId == context.PlayerId)?.Seat;
        if (state.Pawns.FirstOrDefault(pawn => pawn.Seat == seat) is not { } pawn
            || pawn.Tile < 0
            || pawn.Tile >= rules.Tiles.Count
            || pawn.Mon is not (>= 0 and <= LifeRules.MaxMon))
        {
            return MatchRuleDecision.Reject("game_state_unavailable");
        }

        LifeMove move;
        if (pawn.Tile == rules.Finish.Index)
        {
            move = new LifeMove(pawn.Seat, null, pawn.Tile, pawn.Tile, []);
        }
        else
        {
            var roll = context.Random.Next(1, rules.DieFaces + 1);
            var path = rules.Path(pawn.Tile, roll);
            var change = rules.Resolve(path[^1], pawn.Mon!.Value, context.Random);
            move = new LifeMove(pawn.Seat, roll, pawn.Tile, path[^1], path, change);
        }

        var pawns = state.Pawns
            .Select(p => p.Seat == pawn.Seat ? p with { Tile = move.To, Mon = p.Mon + (move.Change ?? 0) } : p)
            .ToArray();
        var next = state with { Pawns = pawns, LastMove = move };
        return MatchRuleDecision.Accept(JsonSerializer.SerializeToElement(next, Json), endsTurn: true);
    }

    public JsonElement? TimeOutTurn(MatchTurnContext context) => null;

    /// <summary>
    /// The board and the pawns with each player's liquid MON, the same for every player: Chog Life has no hidden
    /// information on the board. Pawns of players who left are taken off. A state this server can't read, or of a version it no longer knows, shows nothing.
    /// </summary>
    public JsonElement? ViewFor(MatchViewContext context)
    {
        if (Read(context.State) is not { } state || !_rulesByVersion.TryGetValue(state.RulesVersion, out var rules))
        {
            return null;
        }

        var playing = context.Match.Members
            .Where(member => member is { HasLeft: false, Seat: not null })
            .Select(member => member.Seat!.Value)
            .ToHashSet();
        var view = new LifeBoardView(
            rules.Version,
            rules.Tiles.Select(ToView).ToArray(),
            WithMon(state, rules).Pawns.Where(pawn => playing.Contains(pawn.Seat)).OrderBy(pawn => pawn.Seat).ToArray(),
            state.LastMove);
        return JsonSerializer.SerializeToElement(view, Json);
    }

    private static LifeMatchState? Read(JsonElement state)
    {
        try
        {
            return state.Deserialize<LifeMatchState>(Json) is { Pawns: not null } read ? read : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Pawns stored before STORY-038 have no MON yet; they hold the starting MON.</summary>
    private static LifeMatchState WithMon(LifeMatchState state, LifeRules rules) =>
        state with { Pawns = state.Pawns.Select(pawn => pawn with { Mon = pawn.Mon ?? rules.StartingMon }).ToArray() };

    private static LifeTileView ToView(LifeTile tile) => new(
        tile.Index,
        tile.Type,
        tile.Lane,
        tile.Next,
        tile.SafeNext,
        tile.DegenNext,
        tile.Amount == 0 ? null : tile.Amount,
        tile.Event);

    /// <summary>
    /// The stored state: the rules version the match started with, where each seat's pawn stands with its liquid MON,
    /// and the latest move.
    /// </summary>
    private sealed record LifeMatchState(int RulesVersion, IReadOnlyList<LifePawn> Pawns, LifeMove? LastMove = null);

    /// <summary>
    /// A seat's move: the roll (null when a pawn on the Finish passed), the tiles it left and reached, every tile it
    /// passed on the way, ending with <paramref name="To"/>, and the signed MON the tile it stopped on moved (null for a
    /// tile that does nothing).
    /// </summary>
    private sealed record LifeMove(int Seat, int? Roll, int From, int To, IReadOnlyList<int> Path, int? Change = null);

    /// <summary>A seat's pawn: the tile it stands on and the player's liquid MON (null in states from before STORY-038).</summary>
    private sealed record LifePawn(int Seat, int Tile, int? Mon);

    private sealed record LifeBoardView(
        int RulesVersion, IReadOnlyList<LifeTileView> Tiles, IReadOnlyList<LifePawn> Pawns, LifeMove? LastMove);

    private sealed record LifeTileView(
        int Index,
        LifeTileType Type,
        LifeLane Lane,
        int? Next,
        int? SafeNext,
        int? DegenNext,
        int? Amount,
        LifeEventKind? Event);
}
