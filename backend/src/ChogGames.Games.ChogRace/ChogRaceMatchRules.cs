using System.Text.Json;
using System.Text.Json.Serialization;
using ChogGames.Core.Games;
using ChogGames.Core.Matches;
using ChogGames.Games.ChogRace.Rules;

namespace ChogGames.Games.ChogRace;

/// <summary>
/// Chog Race in the shared match engine (STORY-050, DEC-054). The match starts with every seated player's pawn on the
/// start tile, under the current rules version, which the state records so the match keeps its track after a new version
/// ships. Each player sees the track of that version and the pawns of the players still playing; the turn and seats come
/// with the room snapshot. There are no moves yet (movement comes with STORY-051), so every command is unknown and a turn
/// that runs out is skipped. The time cap is part of the rules but ends matches only with STORY-056.
/// </summary>
public sealed class ChogRaceMatchRules : IMatchRules
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly Dictionary<int, RaceRules> _rulesByVersion;
    private readonly RaceRules _current;

    /// <param name="versions">Every rules version a match may still be playing; new matches use the highest.</param>
    public ChogRaceMatchRules(IEnumerable<RaceRules> versions)
    {
        ArgumentNullException.ThrowIfNull(versions);
        _rulesByVersion = versions.ToDictionary(rules => rules.Version);
        _current = _rulesByVersion.Count > 0
            ? _rulesByVersion[_rulesByVersion.Keys.Max()]
            : throw new ArgumentException("Chog Race needs at least one rules version.", nameof(versions));
    }

    public string GameType => GameDefinition.ChogRace;

    public JsonElement? StartState(MatchStartContext context)
    {
        var pawns = context.Match.Members
            .Where(member => member.Seat is not null)
            .OrderBy(member => member.Seat)
            .Select(member => new RacePawn(member.Seat!.Value, _current.Start.Index))
            .ToArray();
        return JsonSerializer.SerializeToElement(new RaceMatchState(_current.Version, pawns), Json);
    }

    public MatchRuleDecision Apply(MatchCommandContext context) => MatchRuleDecision.Unknown;

    public JsonElement? TimeOutTurn(MatchTurnContext context) => null;

    /// <summary>
    /// The track and the pawns, the same for every player: nothing on the track is hidden (hands come with STORY-052).
    /// Pawns of players who left are taken off. A state this server can't read, or of a version it no longer knows, shows
    /// nothing.
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
        var view = new RaceBoardView(
            rules.Version,
            rules.Tiles.Select(tile => new RaceTileView(tile.Index, tile.Type, tile.To)).ToArray(),
            state.Pawns.Where(pawn => playing.Contains(pawn.Seat)).OrderBy(pawn => pawn.Seat).ToArray());
        return JsonSerializer.SerializeToElement(view, Json);
    }

    private static RaceMatchState? Read(JsonElement state)
    {
        try
        {
            return state.Deserialize<RaceMatchState>(Json) is { Pawns: not null } read && !read.Pawns.Contains(null!) ? read : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The stored state: the rules version the match started with and where each seat's pawn stands.</summary>
    private sealed record RaceMatchState(int RulesVersion, IReadOnlyList<RacePawn> Pawns);

    private sealed record RacePawn(int Seat, int Tile);

    private sealed record RaceBoardView(int RulesVersion, IReadOnlyList<RaceTileView> Tiles, IReadOnlyList<RacePawn> Pawns);

    private sealed record RaceTileView(int Index, RaceTileType Type, int? To);
}
