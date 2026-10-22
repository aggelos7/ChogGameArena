using System.Text.Json;
using System.Text.Json.Serialization;
using ChogGames.Core.Games;
using ChogGames.Core.Matches;
using ChogGames.Core.Rooms;
using ChogGames.Games.ChogRoyale.Abilities;
using ChogGames.Games.ChogRoyale.Rules;

namespace ChogGames.Games.ChogRoyale;

/// <summary>
/// Chog Royale in the shared match engine, played in simultaneous rounds (STORY-070, STORY-071, DEC-081, DEC-082). The
/// match starts with every seated player holding the rules version's starting MON, under the current rules version, which
/// the state records (ROYALE-059). Each player is dealt a hand from the version's weighted deck with match-unique card ids,
/// drawn from the server's randomness (STORY-073, ROYALE-015–020, DEC-084). In each round every player still playing sends
/// <c>SUBMIT_ACTION</c>: a card from their hand, with a target for a targeted card, or HODL, revisable until they lock it
/// or the deadline passes (STORY-072, ROYALE-005–008, 016–018, DEC-083); a player who submits nothing HODLs. Nobody sees
/// another player's hand or pending action, only the public ledger of resolved rounds. Each round resolves in the
/// documented phase order against a snapshot taken before any effect, with seat priority rotated each round, and adds a
/// ledger entry per player (STORY-074, ROYALE-026–032, DEC-085); a played card is replaced afterwards, except after the
/// last round. The cards move MON (STORY-075, STORY-076, DEC-086, DEC-087) under a shared market event drawn and shown
/// before each round's decisions (STORY-077, DEC-088). The match ends after its last round, or when its time cap is up,
/// ranked by MON, then fewer MON lost to opponents, then more successful cards (STORY-078, ROYALE-009, DEC-089). Each
/// player may use the ability they locked once, while deciding (STORY-080, DEC-091).
/// </summary>
public sealed partial class ChogRoyaleMatchRules : IRoundMatchRules
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>A player's action for the round: <c>{"card", "target"}</c> or <c>{"hodl": true}</c>, and <c>"lock"</c> (DEC-083).</summary>
    public const string SubmitAction = "SUBMIT_ACTION";

    private const string Unavailable = "game_state_unavailable";

    private readonly Dictionary<int, RoyaleRules> _rulesByVersion;
    private readonly RoyaleRules _current;

    /// <param name="versions">Every rules version a match may still be playing; new matches use the highest.</param>
    public ChogRoyaleMatchRules(IEnumerable<RoyaleRules> versions)
    {
        ArgumentNullException.ThrowIfNull(versions);
        _rulesByVersion = versions.ToDictionary(rules => rules.Version);
        _current = _rulesByVersion.Count > 0
            ? _rulesByVersion[_rulesByVersion.Keys.Max()]
            : throw new ArgumentException("Chog Royale needs at least one rules version.", nameof(versions));
    }

    public string GameType => GameDefinition.ChogRoyale;

    /// <summary>The current version's round timing; a match keeps the decision window it started with (DEC-081).</summary>
    public MatchRoundTiming RoundTiming => _current.Timing.ToRounds();

    public TimeSpan? TimeLimit => _current.TimeCap;

    /// <summary>
    /// Every seated player with the starting MON and a full hand, dealt in seat order, one weighted draw per card
    /// (DEC-084), then round 1's market event (DEC-088).
    /// </summary>
    public JsonElement? StartState(MatchStartContext context)
    {
        var nextCard = 1;
        var players = context.Match.Members
            .Where(member => member.Seat is not null)
            .OrderBy(member => member.Seat)
            .Select(member => new RoyalePlayer(
                member.Seat!.Value, _current.StartingMon, Deal(_current, _current.HandSize, ref nextCard, context.Random)))
            .ToArray();
        var firstEvent = DrawEvent(_current, context.Random);
        return Serialize(new RoyaleMatchState(_current.Version, 1, firstEvent, nextCard, players, []));
    }

    /// <summary>
    /// <c>SUBMIT_ACTION</c> (DEC-083): the player's action for the round replaces any earlier one; <c>"lock": true</c> makes
    /// it final. Refused, changing nothing: <c>invalid_action</c> (not exactly one of a card or HODL, or a value of the
    /// wrong type), <c>card_not_in_hand</c>, <c>target_required</c>, <c>invalid_target</c> (the player themselves, a seat
    /// not playing, or a target for an untargeted card) and <c>already_submitted</c> (the same action again, without
    /// locking it).
    /// </summary>
    public MatchRuleDecision Apply(MatchCommandContext context)
    {
        if (context.Type != SubmitAction)
        {
            return MatchRuleDecision.Unknown;
        }

        if (Playable(context.State, context.Match) is not var (state, _)
            || SeatOf(context.Match, context.PlayerId) is not { } seat
            || state.Players.FirstOrDefault(p => p.Seat == seat) is not { } player)
        {
            return MatchRuleDecision.Reject(Unavailable);
        }

        if (ReadAction(context.Payload) is not var (action, locks))
        {
            return MatchRuleDecision.Reject("invalid_action");
        }

        if (Refusal(action, player, context.Match) is { } refusal)
        {
            return MatchRuleDecision.Reject(refusal);
        }

        if (action == player.Action && !locks)
        {
            return MatchRuleDecision.Reject("already_submitted");
        }

        var players = state.Players.Select(p => p.Seat == seat ? p with { Action = action } : p).ToArray();
        return MatchRuleDecision.Submit(Serialize(state with { Players = players }), locks);
    }

    /// <summary>Rounds have no turns to time out (DEC-081).</summary>
    public JsonElement? TimeOutTurn(MatchTurnContext context) => null;

    /// <summary>
    /// The time cap is up (ENG-011): the round in play is dropped unresolved and the players are ranked as it stands, by
    /// the same tie-breaks as after the last round. A state this server can't play ranks everyone first together.
    /// </summary>
    public MatchEnding? EndMatch(MatchEndContext context)
    {
        if (Readable(context.State) is not { } state || !_rulesByVersion.TryGetValue(state.RulesVersion, out var rules))
        {
            return new MatchEnding(
                context.State is { ValueKind: JsonValueKind.Object } unreadable ? unreadable : Serialize(new { }),
                [.. Playing(context.Match).Select(member => new MatchStanding(member.PlayerId, 1))]);
        }

        var players = state.Players.Select(p => p with { Action = null }).ToArray();
        return new MatchEnding(Serialize(state with { Players = players }), Standings(context.Match, rules, players, state.Ledger));
    }

    /// <summary>
    /// What one player sees (ROYALE-048, ROYALE-050, ROYALE-060): the round and its market event (ROYALE-021), every
    /// player still playing with their MON and how many cards they hold, the ledger, and only their own hand, pending
    /// action and the ability they used this round, with the next round's event if it was Alpha Leak (DEC-091). A player
    /// who left sees no hand. Null for a state this server can't read.
    /// </summary>
    public JsonElement? ViewFor(MatchViewContext context)
    {
        if (Readable(context.State) is not { } state || !_rulesByVersion.TryGetValue(state.RulesVersion, out var rules))
        {
            return null;
        }

        var playing = Playing(context.Match).Select(m => m.Seat!.Value).ToHashSet();
        var viewerSeat = SeatOf(context.Match, context.PlayerId);
        var viewer = playing.Contains(viewerSeat ?? 0) ? state.Players.FirstOrDefault(p => p.Seat == viewerSeat) : null;
        return Serialize(new RoyaleView(
            state.RulesVersion,
            state.Round,
            rules.Rounds,
            state.Event,
            [.. state.Players.Where(p => playing.Contains(p.Seat)).Select(p => new RoyalePlayerView(p.Seat, p.Mon, p.Hand.Length))],
            viewer?.Hand ?? [],
            viewer?.Action,
            state.Ledger,
            viewer?.Ability?.Id,
            viewer?.Ability?.Id == RoyaleAbilities.AlphaLeak ? state.NextEvent : null));
    }

    /// <summary>What a player sees of the match (DEC-083, DEC-091).</summary>
    private sealed record RoyaleView(
        int RulesVersion,
        int Round,
        int Rounds,
        RoyaleMarketEvent Event,
        RoyalePlayerView[] Players,
        RoyaleHandCard[] Hand,
        RoyaleAction? Action,
        RoyaleRoundLedger[] Ledger,
        string? Ability,
        RoyaleMarketEvent? NextEvent);

    /// <summary>Another player as everyone sees them: never their cards, only how many.</summary>
    private sealed record RoyalePlayerView(int Seat, int Mon, int Cards);

    private static RoyaleHandCard[] Deal(RoyaleRules rules, int count, ref int nextCard, IRandomSource random)
    {
        var cards = new RoyaleHandCard[count];
        for (var i = 0; i < count; i++)
        {
            cards[i] = new RoyaleHandCard($"c{nextCard++}", rules.CardAt(random.Next(0, rules.TotalWeight)));
        }

        return cards;
    }

    /// <summary>The payload's action and lock, or null when it isn't exactly one well-formed card or HODL.</summary>
    private static (RoyaleAction Action, bool Locks)? ReadAction(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        bool? hodl = null;
        string? card = null;
        int? target = null;
        var locks = false;
        foreach (var property in payload.EnumerateObject())
        {
            switch (property.Name, property.Value.ValueKind)
            {
                case ("hodl", JsonValueKind.True or JsonValueKind.False):
                    hodl = property.Value.GetBoolean();
                    break;
                case ("card", JsonValueKind.String):
                    card = property.Value.GetString();
                    break;
                case ("target", JsonValueKind.Number) when property.Value.TryGetInt32(out var seat):
                    target = seat;
                    break;
                case ("target", JsonValueKind.Null) or ("card", JsonValueKind.Null):
                    break;
                case ("lock", JsonValueKind.True or JsonValueKind.False):
                    locks = property.Value.GetBoolean();
                    break;
                default:
                    return null;
            }
        }

        return (hodl == true, card) switch
        {
            (true, null) when target is null => (new RoyaleAction(Hodl: true), locks),
            (false, { Length: > 0 }) => (new RoyaleAction(Card: card, Target: target), locks),
            _ => null,
        };
    }

    private static string? Refusal(RoyaleAction action, RoyalePlayer player, GameRoom match)
    {
        if (action.Hodl)
        {
            return null;
        }

        if (player.Hand.FirstOrDefault(c => c.Id == action.Card) is not { } card)
        {
            return "card_not_in_hand";
        }

        if (!card.Card.IsTargeted())
        {
            return action.Target is null ? null : "invalid_target";
        }

        if (action.Target is not { } target)
        {
            return "target_required";
        }

        return target != player.Seat && Playing(match).Any(m => m.Seat == target) ? null : "invalid_target";
    }

    /// <summary>A state this server can play for the match's current round, with its rules version.</summary>
    private (RoyaleMatchState State, RoyaleRules Rules)? Playable(JsonElement? json, GameRoom match) =>
        Readable(json) is { } state
        && _rulesByVersion.TryGetValue(state.RulesVersion, out var rules)
        && state.Round == match.TurnNumber
        && state.Round <= rules.Rounds
        && Enum.IsDefined(state.Event)
        && (state.NextEvent is null || Enum.IsDefined(state.NextEvent.Value))
        && state.Players.All(p => p.Mon >= 0 && p.Mon <= rules.MaxMon && p.Hand.Length <= rules.HandSize && p.Ability is null or { Value: >= 0 })
            ? (state, rules)
            : null;

    private static RoyaleMatchState? Readable(JsonElement? json)
    {
        if (json is not { ValueKind: JsonValueKind.Object } state)
        {
            return null;
        }

        try
        {
            return state.Deserialize<RoyaleMatchState>(Json) is { Players: not null, Ledger: not null } read
                && read.Players.All(p => p.Hand is not null)
                ? read
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static IEnumerable<RoomMember> Playing(GameRoom match) =>
        match.Members.Where(m => !m.HasLeft && m.Seat is not null).OrderBy(m => m.Seat);

    private static int? SeatOf(GameRoom match, Guid playerId) => match.Members.SingleOrDefault(m => m.PlayerId == playerId)?.Seat;

    /// <summary>
    /// Ranks every player still playing (STORY-078, ROYALE-009, DEC-089): by MON, highest first, then fewer MON lost to
    /// other players' cards over the match, then more successful cards (played and <see cref="RoyaleOutcome.Applied"/>);
    /// players equal on all three share a place. Each result carries their MON, its change from the start and both
    /// tie-break values.
    /// </summary>
    private static List<MatchStanding> Standings(
        GameRoom match, RoyaleRules rules, IReadOnlyList<RoyalePlayer> players, IReadOnlyList<RoyaleRoundLedger> ledger)
    {
        var entries = ledger.SelectMany(round => round.Entries).ToList();
        var results = Playing(match)
            .Select(member => new RoyaleResult(
                member.PlayerId,
                players.FirstOrDefault(p => p.Seat == member.Seat)?.Mon ?? 0,
                entries.Where(e => e.Seat == member.Seat).Sum(e => e.Lost),
                entries.Count(e => e.Seat == member.Seat && e.Outcome == RoyaleOutcome.Applied)))
            .ToList();
        return
        [
            .. results.Select(result => new MatchStanding(
                result.PlayerId,
                1 + results.Count(other => other.Beats(result)),
                Serialize(new { mon = result.Mon, change = result.Mon - rules.StartingMon, lost = result.Lost, successfulCards = result.SuccessfulCards }))),
        ];
    }

    private sealed record RoyaleResult(Guid PlayerId, int Mon, int Lost, int SuccessfulCards)
    {
        /// <summary>Whether this result ranks ahead of <paramref name="other"/>.</summary>
        public bool Beats(RoyaleResult other) =>
            (Mon, -Lost, SuccessfulCards).CompareTo((other.Mon, -other.Lost, other.SuccessfulCards)) > 0;
    }

    private static JsonElement Serialize<T>(T value) => JsonSerializer.SerializeToElement(value, Json);
}
