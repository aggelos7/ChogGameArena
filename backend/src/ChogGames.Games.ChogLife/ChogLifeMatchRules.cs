using System.Text.Json;
using System.Text.Json.Serialization;
using ChogGames.Core.Games;
using ChogGames.Core.Matches;
using ChogGames.Games.ChogLife.Rules;

namespace ChogGames.Games.ChogLife;

/// <summary>
/// Chog Life in the shared match engine (STORY-036, DEC-038). The match starts with every seated player's pawn on the
/// start tile, under the current rules version, which the state records so the match keeps its board after a new version
/// ships (DEC-037). Each player sees the board of that version and the pawns of the players still playing; the turn and
/// seats come with the room snapshot. There are no moves yet (rolling comes with STORY-037), so every command is unknown
/// and a turn that runs out is skipped.
/// </summary>
public sealed class ChogLifeMatchRules : IMatchRules
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

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

    public JsonElement? StartState(MatchStartContext context)
    {
        var pawns = context.Match.Members
            .Where(member => member.Seat is not null)
            .OrderBy(member => member.Seat)
            .Select(member => new LifePawn(member.Seat!.Value, _current.Start.Index))
            .ToArray();
        return JsonSerializer.SerializeToElement(new LifeMatchState(_current.Version, pawns), Json);
    }

    public MatchRuleDecision Apply(MatchCommandContext context) => MatchRuleDecision.Unknown;

    public JsonElement? TimeOutTurn(MatchTurnContext context) => null;

    /// <summary>
    /// The board and the pawns, the same for every player: Chog Life has no hidden information on the board. Pawns of
    /// players who left are taken off. A state this server can't read, or of a version it no longer knows, shows nothing.
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
            state.Pawns.Where(pawn => playing.Contains(pawn.Seat)).OrderBy(pawn => pawn.Seat).ToArray());
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

    private static LifeTileView ToView(LifeTile tile) => new(
        tile.Index,
        tile.Type,
        tile.Lane,
        tile.Next,
        tile.SafeNext,
        tile.DegenNext,
        tile.Amount == 0 ? null : tile.Amount,
        tile.Event);

    /// <summary>The stored state: the rules version the match started with and where each seat's pawn stands.</summary>
    private sealed record LifeMatchState(int RulesVersion, IReadOnlyList<LifePawn> Pawns);

    private sealed record LifePawn(int Seat, int Tile);

    private sealed record LifeBoardView(int RulesVersion, IReadOnlyList<LifeTileView> Tiles, IReadOnlyList<LifePawn> Pawns);

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
