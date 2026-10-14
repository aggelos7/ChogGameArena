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
/// their MON and an event tile draws one of its outcomes, and every player sees each player's liquid MON. A roll that
/// stops on a fork keeps the turn until the player sends <c>CHOOSE_LANE</c> (STORY-039, DEC-041); their next roll
/// leaves by that lane. A turn that runs out while that choice is pending takes the rules' default lane; any other turn
/// that runs out is skipped.
/// </summary>
public sealed class ChogLifeMatchRules : IMatchRules
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Roll the die, move and resolve the tile; it ends the turn unless the pawn stops on a fork.</summary>
    public const string Roll = "ROLL";

    /// <summary>
    /// Choose the lane to leave a fork by, with <c>{"lane": "Safe"}</c> or <c>{"lane": "Degen"}</c>, after a roll that
    /// stopped there (STORY-039, DEC-041); it ends the turn.
    /// </summary>
    public const string ChooseLane = "CHOOSE_LANE";

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
    /// pawn moves along <see cref="LifeRules.Path"/>, leaving a fork by the lane its player chose there, the tile it
    /// stops on resolves with <see cref="LifeRules.Resolve"/> (an event's draw comes after the die's) and the turn ends,
    /// unless the pawn stopped on a fork: then the player chooses a lane first (DEC-041). A pawn on the Finish doesn't
    /// move and draws nothing; its roll just passes the turn until STORY-041 decides what finished players do. A match
    /// started before its state existed (before STORY-036) gets its start state first, and pawns from before STORY-038
    /// hold the starting MON. <c>CHOOSE_LANE</c> records the chosen lane on the pawn and ends the turn.
    /// </summary>
    public MatchRuleDecision Apply(MatchCommandContext context)
    {
        if (context.Type is not (Roll or ChooseLane))
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
            || pawn.Mon is not (>= 0 and <= LifeRules.MaxMon)
            || pawn.Lane is not (null or LifeLane.Safe or LifeLane.Degen))
        {
            return MatchRuleDecision.Reject("game_state_unavailable");
        }

        // A pending choice belongs to the turn it was made in; one left by a player who has since gone counts for nobody.
        var choosing = state.Choosing == pawn.Seat;
        state = state with { Choosing = null };
        return context.Type == Roll
            ? RollFor(pawn, choosing, state, rules, context.Random)
            : ChooseFor(pawn, choosing, state, rules, context.Payload);
    }

    private static MatchRuleDecision RollFor(
        LifePawn pawn, bool choosing, LifeMatchState state, LifeRules rules, IRandomSource random)
    {
        if (choosing)
        {
            return MatchRuleDecision.Reject("choose_lane_first");
        }

        LifeMove move;
        if (pawn.Tile == rules.Finish.Index)
        {
            move = new LifeMove(pawn.Seat, null, pawn.Tile, pawn.Tile, []);
        }
        else
        {
            var roll = random.Next(1, rules.DieFaces + 1);
            var path = rules.Path(pawn.Tile, roll, pawn.Lane);
            var change = rules.Resolve(path[^1], pawn.Mon!.Value, random);
            move = new LifeMove(pawn.Seat, roll, pawn.Tile, path[^1], path, change);
        }

        var atFork = rules.Tiles[move.To].Type == LifeTileType.Branch;
        var next = state with
        {
            Pawns = Replace(state, pawn with { Tile = move.To, Mon = pawn.Mon + (move.Change ?? 0), Lane = null }),
            LastMove = move,
            Choosing = atFork ? pawn.Seat : null,
        };
        return MatchRuleDecision.Accept(JsonSerializer.SerializeToElement(next, Json), endsTurn: !atFork);
    }

    /// <summary>
    /// Only the two lanes the fork offers are accepted, by name as the board shows them (<c>Safe</c>, <c>Degen</c>);
    /// anything else, or a choice with none pending, changes nothing.
    /// </summary>
    private static MatchRuleDecision ChooseFor(
        LifePawn pawn, bool choosing, LifeMatchState state, LifeRules rules, JsonElement payload)
    {
        if (!choosing || rules.Tiles[pawn.Tile].Type != LifeTileType.Branch)
        {
            return MatchRuleDecision.Reject("no_choice_pending");
        }

        if (LaneIn(payload) is not { } lane)
        {
            return MatchRuleDecision.Reject("invalid_lane");
        }

        var chosen = Choose(state, pawn, lane, byDefault: false);
        return MatchRuleDecision.Accept(JsonSerializer.SerializeToElement(chosen, Json), endsTurn: true);
    }

    private static LifeLane? LaneIn(JsonElement payload) =>
        payload.ValueKind == JsonValueKind.Object
        && payload.TryGetProperty("lane", out var lane)
        && lane.ValueKind == JsonValueKind.String
            ? lane.GetString() switch
            {
                nameof(LifeLane.Safe) => LifeLane.Safe,
                nameof(LifeLane.Degen) => LifeLane.Degen,
                _ => null,
            }
            : null;

    /// <summary>The pawn keeps its place on the fork with the lane it will leave by, and the last move says which.</summary>
    private static LifeMatchState Choose(LifeMatchState state, LifePawn pawn, LifeLane lane, bool byDefault) => state with
    {
        Pawns = Replace(state, pawn with { Lane = lane }),
        LastMove = state.LastMove is { } move && move.Seat == pawn.Seat
            ? move with { Lane = lane, ByDefault = byDefault ? true : null }
            : state.LastMove,
        Choosing = null,
    };

    private static LifePawn[] Replace(LifeMatchState state, LifePawn pawn) =>
        state.Pawns.Select(p => p.Seat == pawn.Seat ? pawn : p).ToArray();

    /// <summary>
    /// A turn that runs out while its player is choosing a lane takes the rules' default lane (DEC-041); any other turn
    /// is skipped with the state unchanged, and so is a state this server can't play.
    /// </summary>
    public JsonElement? TimeOutTurn(MatchTurnContext context)
    {
        if (context.State is not { } json
            || Read(json) is not { Choosing: { } seat } state
            || !_rulesByVersion.TryGetValue(state.RulesVersion, out var rules)
            || context.Match.Members.SingleOrDefault(member => member.PlayerId == context.Match.CurrentPlayerId)?.Seat != seat
            || state.Pawns.FirstOrDefault(pawn => pawn.Seat == seat) is not { } pawn
            || pawn.Tile < 0
            || pawn.Tile >= rules.Tiles.Count
            || rules.Tiles[pawn.Tile].Type != LifeTileType.Branch)
        {
            return null;
        }

        return JsonSerializer.SerializeToElement(Choose(state, pawn, rules.DefaultLane, byDefault: true), Json);
    }

    /// <summary>
    /// The board and the pawns with each player's liquid MON and the lane chosen at the fork they stand on, who is
    /// choosing a lane and the lane taken if they don't, the same for every player: Chog Life has no hidden information
    /// on the board. Pawns of players who left are taken off. A state this server can't read, or of a version it no longer knows, shows nothing.
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
            state.LastMove,
            state.Choosing is { } seat && playing.Contains(seat) ? seat : null,
            rules.DefaultLane);
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
    /// the latest move and the seat whose roll stopped on a fork and who is still to choose a lane.
    /// </summary>
    private sealed record LifeMatchState(
        int RulesVersion, IReadOnlyList<LifePawn> Pawns, LifeMove? LastMove = null, int? Choosing = null);

    /// <summary>
    /// A seat's move: the roll (null when a pawn on the Finish passed), the tiles it left and reached, every tile it
    /// passed on the way, ending with <paramref name="To"/>, and the signed MON the tile it stopped on moved (null for a
    /// tile that does nothing). A move that stopped on a fork gets the <paramref name="Lane"/> chosen there, and
    /// <paramref name="ByDefault"/> when the turn ran out and the rules chose it.
    /// </summary>
    private sealed record LifeMove(
        int Seat,
        int? Roll,
        int From,
        int To,
        IReadOnlyList<int> Path,
        int? Change = null,
        LifeLane? Lane = null,
        bool? ByDefault = null);

    /// <summary>
    /// A seat's pawn: the tile it stands on, the player's liquid MON (null in states from before STORY-038) and, on a
    /// fork, the lane it will leave by (null until chosen).
    /// </summary>
    private sealed record LifePawn(int Seat, int Tile, int? Mon, LifeLane? Lane = null);

    private sealed record LifeBoardView(
        int RulesVersion,
        IReadOnlyList<LifeTileView> Tiles,
        IReadOnlyList<LifePawn> Pawns,
        LifeMove? LastMove,
        int? Choosing,
        LifeLane DefaultLane);

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
