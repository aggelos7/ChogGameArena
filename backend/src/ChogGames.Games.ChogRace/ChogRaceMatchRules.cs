using System.Text.Json;
using System.Text.Json.Serialization;
using ChogGames.Core.Games;
using ChogGames.Core.Matches;
using ChogGames.Core.Rooms;
using ChogGames.Games.ChogRace.Rules;

namespace ChogGames.Games.ChogRace;

/// <summary>
/// Chog Race in the shared match engine (STORY-050, DEC-054). The match starts with every seated player's pawn on the
/// start tile, under the current rules version, which the state records so the match keeps its track after a new version
/// ships. Each player sees the track of that version and the pawns of the players still playing; the turn and seats come
/// with the room snapshot. A turn has phases (STORY-051, RACE-004, DEC-055): in the Move phase the player sends
/// <c>ROLL</c>, the server rolls, moves the pawn and resolves the tile it lands on (a Boost or Setback sends it on, a
/// Card tile draws a card); the state keeps that last move, so every player sees the same roll and destination. A
/// player holding a card they can play on their turn then enters the Play phase, for that card (STORY-053/054) and
/// <c>END_TURN</c>; otherwise the roll ends the turn. Commands out of phase are rejected. Cards (STORY-052, RACE-003,
/// DEC-056) come from a deck the server shuffles at the start; a player with a full hand draws nothing, and an empty
/// deck is refilled from the discard pile and the hands of players who left. The deck's order and each hand are
/// hidden: a player sees only their own cards and how many everyone else holds. The first pawn to reach the Finish wins and ends the match, the others ranked by how far they got
/// (tie-breakers, the time cap and results come with STORY-056). A turn that runs out before the roll is skipped; one
/// that runs out in the Play phase ends with no card played.
/// </summary>
public sealed class ChogRaceMatchRules : IMatchRules
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>The Move phase: roll, move and resolve the tile landed on (DEC-055).</summary>
    public const string Roll = "ROLL";

    /// <summary>Ends the Play phase, and with it the turn, without playing a card (DEC-055).</summary>
    public const string EndTurn = "END_TURN";

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
        var deck = _current.Deck.ToArray();
        context.Random.Shuffle(deck);
        return JsonSerializer.SerializeToElement(new RaceMatchState(_current.Version, pawns, Deck: deck, Discard: []), Json);
    }

    /// <summary>
    /// <c>ROLL</c> in the Move phase: one roll from the server's source of chance, never from the payload, which is
    /// ignored (DEC-031). <c>END_TURN</c> in the Play phase ends the turn. Either out of its phase is rejected
    /// (<c>already_rolled</c>, <c>must_roll_first</c>) and changes nothing; a state this server can't play is rejected
    /// as <c>game_state_unavailable</c>.
    /// </summary>
    public MatchRuleDecision Apply(MatchCommandContext context)
    {
        if (context.Type is not (Roll or EndTurn))
        {
            return MatchRuleDecision.Unknown;
        }

        if (context.State is not { } json
            || Read(json) is not { Winner: null } state
            || !_rulesByVersion.TryGetValue(state.RulesVersion, out var rules)
            || !IsPlayable(state, rules)
            || SeatOf(context.Match, context.PlayerId) is not { } seat
            || state.Pawns.FirstOrDefault(pawn => pawn.Seat == seat) is not { } pawn)
        {
            return MatchRuleDecision.Reject("game_state_unavailable");
        }

        // The Play phase belongs to the turn it began in; one left by a player who has since gone counts for nobody.
        var playing = state.PlayPhase == seat;
        if (context.Type == EndTurn)
        {
            return playing
                ? MatchRuleDecision.Accept(JsonSerializer.SerializeToElement(state with { PlayPhase = null }, Json), endsTurn: true)
                : MatchRuleDecision.Reject("must_roll_first");
        }

        return playing ? MatchRuleDecision.Reject("already_rolled") : RollFor(pawn, state, rules, context.Random, context.Match);
    }

    /// <summary>
    /// The pawn moves the roll's spaces, stopping on the Finish rather than passing it (DEC-054), then the tile it lands
    /// on resolves: a Boost or Setback sends it on to its target, which never jumps again, and a Card tile draws its
    /// player a card. Reaching the Finish wins the match.
    /// </summary>
    private static MatchRuleDecision RollFor(RacePawn pawn, RaceMatchState state, RaceRules rules, IRandomSource random, GameRoom match)
    {
        var roll = random.Next(rules.MinMove, rules.MaxMove + 1);
        var landed = Math.Min(pawn.Tile + roll, rules.Finish.Index);
        var tile = rules.Tiles[landed];
        var to = tile.To ?? landed;
        var move = new RaceMove(pawn.Seat, roll, pawn.Tile, landed, to, tile.Type is RaceTileType.Normal ? null : tile.Type);
        var pawns = state.Pawns.Select(p => p.Seat == pawn.Seat ? p with { Tile = to } : p).ToArray();
        var next = state with { Pawns = pawns, PlayPhase = null };
        if (tile.Type is RaceTileType.Card)
        {
            (next, move) = DrawFor(next, move, rules, random, match);
        }

        next = next with { LastMove = move };

        if (to == rules.Finish.Index)
        {
            var won = next with { Winner = pawn.Seat };
            return MatchRuleDecision.Finish(JsonSerializer.SerializeToElement(won, Json), Standings(won, match));
        }

        var play = HoldsCard(next, pawn.Seat);
        return MatchRuleDecision.Accept(
            JsonSerializer.SerializeToElement(play ? next with { PlayPhase = pawn.Seat } : next, Json), endsTurn: !play);
    }

    /// <summary>
    /// The mover draws the deck's top card (DEC-056), unless their hand is full, which leaves the deck as it is. An empty
    /// deck is first refilled: the discard pile and the hands of players who left are shuffled into a new one. With no
    /// card there either, as when every card is in a hand, nothing is drawn.
    /// </summary>
    private static (RaceMatchState State, RaceMove Move) DrawFor(
        RaceMatchState state, RaceMove move, RaceRules rules, IRandomSource random, GameRoom match)
    {
        if (HandOf(state, move.Seat).Count >= rules.MaxHand)
        {
            return (state, move with { Draw = RaceDraw.HandFull });
        }

        var pawns = state.Pawns;
        var deck = state.Deck ?? [];
        var discard = state.Discard ?? [];
        bool? reshuffled = null;
        if (deck.Count == 0)
        {
            var playing = PlayingSeats(match);
            var pool = discard.Concat(pawns.Where(p => !playing.Contains(p.Seat)).SelectMany(p => p.Hand ?? [])).ToArray();
            if (pool.Length == 0)
            {
                return (state, move with { Draw = RaceDraw.NoCards });
            }

            random.Shuffle(pool);
            deck = pool;
            discard = [];
            pawns = pawns.Select(p => playing.Contains(p.Seat) ? p : p with { Hand = null }).ToArray();
            reshuffled = true;
        }

        var card = deck[0];
        pawns = pawns.Select(p => p.Seat == move.Seat ? p with { Hand = [.. p.Hand ?? [], card] } : p).ToArray();
        return (
            state with { Pawns = pawns, Deck = deck.Skip(1).ToArray(), Discard = discard },
            move with { Draw = RaceDraw.Drawn, Card = card, Reshuffled = reshuffled });
    }

    /// <summary>
    /// Whether the seat holds a card it can play on its own turn, which opens the Play phase (DEC-055, DEC-056); a card
    /// played only in reply to another player, like HODL, never does.
    /// </summary>
    private static bool HoldsCard(RaceMatchState state, int seat) => HandOf(state, seat).Any(PlayableOnTurn);

    /// <summary>Whether a card can be played on its holder's turn. None can until their effects exist (STORY-053/054).</summary>
    private static bool PlayableOnTurn(RaceCard card) => false;

    private static IReadOnlyList<RaceCard> HandOf(RaceMatchState state, int seat) =>
        state.Pawns.FirstOrDefault(pawn => pawn.Seat == seat)?.Hand ?? [];

    private static HashSet<int> PlayingSeats(GameRoom match) =>
        match.Members
            .Where(member => member is { HasLeft: false, Seat: not null })
            .Select(member => member.Seat!.Value)
            .ToHashSet();

    /// <summary>
    /// A rank for every player still playing (DEC-055): the winner first, then everyone else by how far along the track
    /// they are, players on the same tile sharing a rank (1, 2, 2, 4). STORY-056 adds the tie-breakers.
    /// </summary>
    private static IReadOnlyList<MatchStanding> Standings(RaceMatchState state, GameRoom match)
    {
        var tiles = state.Pawns.ToDictionary(pawn => pawn.Seat, pawn => pawn.Tile);
        var players = match.Members
            .Where(member => member is { HasLeft: false, Seat: not null })
            .Select(member => (member.PlayerId, Seat: member.Seat!.Value, Tile: tiles.GetValueOrDefault(member.Seat!.Value, -1)))
            .ToArray();
        bool Beats((Guid, int Seat, int Tile) a, (Guid, int Seat, int Tile) b) =>
            a.Seat != b.Seat && (a.Seat == state.Winner || (b.Seat != state.Winner && a.Tile > b.Tile));
        return players
            .Select(player => new MatchStanding(player.PlayerId, 1 + players.Count(other => Beats(other, player))))
            .ToArray();
    }

    /// <summary>
    /// A turn that runs out in the Play phase ends with no card played (DEC-055); one that runs out before the roll, or
    /// with a state this server can't play, is skipped with the state unchanged.
    /// </summary>
    public JsonElement? TimeOutTurn(MatchTurnContext context)
    {
        if (context.State is not { } json
            || Read(json) is not { Winner: null, PlayPhase: { } seat } state
            || SeatOf(context.Match, context.Match.CurrentPlayerId) != seat)
        {
            return null;
        }

        return JsonSerializer.SerializeToElement(state with { PlayPhase = null }, Json);
    }

    /// <summary>
    /// The track, the pawns with how many cards each holds, the last move, the current player's phase, the sizes of the
    /// deck and discard pile and, once the race is won, the winner's seat, the same for every player. Only the viewer's
    /// own hand, and the card they last drew, are shown by name (DEC-056); the deck's order is never shown. Pawns of
    /// players who left are taken off. A state this server can't read, or of a version it no longer knows, shows nothing.
    /// </summary>
    public JsonElement? ViewFor(MatchViewContext context)
    {
        if (Read(context.State) is not { } state || !_rulesByVersion.TryGetValue(state.RulesVersion, out var rules))
        {
            return null;
        }

        var playing = PlayingSeats(context.Match);
        var viewer = SeatOf(context.Match, context.PlayerId) is { } own && playing.Contains(own) ? own : (int?)null;
        RacePhase? phase = context.Match.Status != RoomStatus.Active ? null
            : state.PlayPhase is { } seat && seat == SeatOf(context.Match, context.Match.CurrentPlayerId) ? RacePhase.Play
            : RacePhase.Move;
        var view = new RaceBoardView(
            rules.Version,
            rules.Tiles.Select(tile => new RaceTileView(tile.Index, tile.Type, tile.To)).ToArray(),
            state.Pawns
                .Where(pawn => playing.Contains(pawn.Seat))
                .OrderBy(pawn => pawn.Seat)
                .Select(pawn => new RacePawnView(pawn.Seat, pawn.Tile, pawn.Hand?.Count ?? 0))
                .ToArray(),
            state.LastMove is { } move && move.Seat != viewer ? move with { Card = null } : state.LastMove,
            phase,
            state.Winner,
            rules.MaxHand,
            state.Deck?.Count ?? 0,
            state.Discard?.Count ?? 0,
            viewer is { } mine ? HandOf(state, mine) : null);
        return JsonSerializer.SerializeToElement(view, Json);
    }

    private static int? SeatOf(GameRoom match, Guid? playerId) =>
        match.Members.SingleOrDefault(member => member.PlayerId == playerId)?.Seat;

    /// <summary>Every pawn stands on the track, once per seat, holding no more than a full hand of known cards.</summary>
    private static bool IsPlayable(RaceMatchState state, RaceRules rules) =>
        state.Pawns.All(pawn => pawn.Tile >= 0 && pawn.Tile < rules.Tiles.Count && (pawn.Hand?.Count ?? 0) <= rules.MaxHand)
        && state.Pawns.DistinctBy(pawn => pawn.Seat).Count() == state.Pawns.Count
        && state.Pawns.SelectMany(pawn => pawn.Hand ?? []).Concat(state.Deck ?? []).Concat(state.Discard ?? []).All(Enum.IsDefined);

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

    /// <summary>
    /// The stored state: the rules version the match started with, where each seat's pawn stands and the cards it holds,
    /// the latest move, the seat whose turn is in the Play phase (null in the Move phase), once the race is won the
    /// winner's seat, and the deck, top card first, and discard pile. A match started before cards (STORY-052) has
    /// neither and draws nothing.
    /// </summary>
    private sealed record RaceMatchState(
        int RulesVersion,
        IReadOnlyList<RacePawn> Pawns,
        RaceMove? LastMove = null,
        int? PlayPhase = null,
        int? Winner = null,
        IReadOnlyList<RaceCard>? Deck = null,
        IReadOnlyList<RaceCard>? Discard = null);

    /// <summary>A seat's pawn and its hand, null while empty.</summary>
    private sealed record RacePawn(int Seat, int Tile, IReadOnlyList<RaceCard>? Hand = null);

    /// <summary>A pawn as every player sees it: how many cards it holds, not which.</summary>
    private sealed record RacePawnView(int Seat, int Tile, int Cards);

    /// <summary>
    /// A seat's move: the roll, the tile it left, the tile it <paramref name="Landed"/> on and the tile it ended
    /// <paramref name="To"/>, after a Boost or Setback sent it on, and the <paramref name="Effect"/> of the tile it landed
    /// on (null for a Normal tile). On a Card tile, the <paramref name="Draw"/>, the <paramref name="Card"/> drawn, shown
    /// only to the seat that drew it, and whether the deck was <paramref name="Reshuffled"/> first.
    /// </summary>
    private sealed record RaceMove(
        int Seat,
        int Roll,
        int From,
        int Landed,
        int To,
        RaceTileType? Effect,
        RaceDraw? Draw = null,
        RaceCard? Card = null,
        bool? Reshuffled = null);

    /// <summary>What landing on a Card tile drew (DEC-056).</summary>
    private enum RaceDraw
    {
        /// <summary>A card, now in the mover's hand.</summary>
        Drawn,

        /// <summary>Nothing: the mover's hand was full.</summary>
        HandFull,

        /// <summary>Nothing: the deck and discard pile were empty, every card being in a hand.</summary>
        NoCards,
    }

    /// <summary>A turn's phases (RACE-004, DEC-055); reactions come with DEC-003.</summary>
    private enum RacePhase
    {
        Move,
        Play,
    }

    private sealed record RaceBoardView(
        int RulesVersion,
        IReadOnlyList<RaceTileView> Tiles,
        IReadOnlyList<RacePawnView> Pawns,
        RaceMove? LastMove,
        RacePhase? Phase,
        int? Winner,
        int MaxHand,
        int Deck,
        int Discard,
        IReadOnlyList<RaceCard>? Hand);

    private sealed record RaceTileView(int Index, RaceTileType Type, int? To);
}
