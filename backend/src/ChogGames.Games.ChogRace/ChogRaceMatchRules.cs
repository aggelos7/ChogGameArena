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
/// player holding a card they can play on their turn then enters the Play phase, for <c>PLAY_CARD</c> or <c>END_TURN</c>;
/// otherwise the roll ends the turn. Playing one card ends the turn (STORY-053, DEC-057): Pump and FOMO move the player
/// and Airdrop rolls again, each resolving the tile landed on as a roll does; Whale swaps tiles with an opponent the player
/// picks and Reverse moves every opponent back, a pushed pawn following the Boost or Setback it lands on. Rug moves an
/// opponent back and Liquidated makes them skip their next turn (STORY-054, DEC-058); a Rug, Liquidated or Whale on a
/// player holding cards waits for their reply, HODL to block it or <c>PASS</c> to let it land, and lands when the reply
/// runs out. Commands out of phase are rejected. Cards (STORY-052, RACE-003,
/// DEC-056) come from a deck the server shuffles at the start; a player with a full hand draws nothing, and an empty
/// deck is refilled from the discard pile and the hands of players who left. The deck's order and each hand are
/// hidden: a player sees only their own cards and how many everyone else holds. The first pawn to reach the Finish wins
/// and ends the match; the 20-minute cap ends it with the furthest pawn first, and each player gets a result (STORY-056,
/// DEC-060). A turn that runs out before the roll is skipped; one
/// that runs out in the Play phase ends with no card played. Each player's ability (STORY-055, DEC-059) acts on this state:
/// Speed Boost adds to a roll, a Shield blocks an attack with no reply window, Card Vision shows one player the deck's top
/// card and Card Steal moves a card between hands.
/// </summary>
public sealed partial class ChogRaceMatchRules : IMatchRules
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

    /// <summary>
    /// Plays a card from the hand in the Play phase, which ends the turn (DEC-057), or HODL in reply to an attack
    /// (DEC-058).
    /// </summary>
    public const string PlayCard = "PLAY_CARD";

    /// <summary>The target of an attack lets it land instead of blocking it (DEC-058).</summary>
    public const string Pass = "PASS";

    /// <summary>Why only the attack's target may act while the turn waits for their reply (DEC-058).</summary>
    private const string ReplyPending = "reply_pending";

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
    /// ignored (DEC-031). <c>END_TURN</c> and <c>PLAY_CARD</c> in the Play phase end the turn. Any of them out of its phase
    /// is rejected (<c>already_rolled</c>, <c>must_roll_first</c>) and changes nothing; a state this server can't play is
    /// rejected as <c>game_state_unavailable</c>.
    /// </summary>
    public MatchRuleDecision Apply(MatchCommandContext context)
    {
        if (context.Type is not (Roll or EndTurn or PlayCard or Pass))
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

        // While the turn waits for an attack's target, only they reach the rules (DEC-058); a window the engine closed,
        // because a player left, is void.
        var reply = LiveReply(state, context.Match);
        state = state with { Reply = reply };
        if (reply is not null)
        {
            return seat == reply.Target
                ? ReplyFor(pawn, reply, state, rules, context)
                : MatchRuleDecision.Reject(ReplyPending);
        }

        if (context.Type == Pass)
        {
            return MatchRuleDecision.Reject("no_attack");
        }

        // The Play phase belongs to the turn it began in; one left by a player who has since gone counts for nobody.
        var playing = state.PlayPhase == seat;
        if (context.Type == EndTurn)
        {
            return playing
                ? MatchRuleDecision.Accept(JsonSerializer.SerializeToElement(state with { PlayPhase = null }, Json), endsTurn: true)
                : MatchRuleDecision.Reject("must_roll_first");
        }

        if (context.Type == PlayCard)
        {
            return playing
                ? PlayFor(pawn, state, rules, context.Payload, context.Random, context.Match)
                : MatchRuleDecision.Reject("must_roll_first");
        }

        return playing ? MatchRuleDecision.Reject("already_rolled") : RollFor(pawn, state, rules, context.Random, context.Match);
    }

    /// <summary>
    /// The pawn moves the roll's spaces, and Speed Boost's tiles if armed (DEC-059), stopping on the Finish rather than
    /// passing it (DEC-054), then the tile it lands on resolves: a Boost or Setback sends it on to its target, which never jumps again, and a Card tile, landed on or
    /// jumped to, draws its player a card. Reaching the Finish wins the match.
    /// </summary>
    private static MatchRuleDecision RollFor(RacePawn pawn, RaceMatchState state, RaceRules rules, IRandomSource random, GameRoom match)
    {
        var roll = random.Next(rules.MinMove, rules.MaxMove + 1);
        // Speed Boost adds its tiles to this roll and is spent (DEC-059); another player's ability use is now old news.
        var boosted = state with
        {
            PlayPhase = null,
            Pawns = Replace(state.Pawns, pawn with { Boost = null }),
            Ability = state.Ability?.Seat == pawn.Seat ? state.Ability : null,
        };
        var (next, landing) = MoveBy(boosted, pawn.Seat, roll + (pawn.Boost ?? 0), rules, random, match);
        next = Arrived(state, next) with
        {
            LastMove = new RaceMove(
                pawn.Seat, roll, landing.From, landing.Landed, landing.To, landing.Effect, landing.Draw, landing.Card, landing.Reshuffled)
            {
                Boost = pawn.Boost,
            },
        };

        if (Won(next, [pawn.Seat], rules, match) is { } won)
        {
            return won;
        }

        var play = HoldsCard(next, pawn.Seat);
        return MatchRuleDecision.Accept(
            JsonSerializer.SerializeToElement(play ? next with { PlayPhase = pawn.Seat } : next, Json), endsTurn: !play);
    }

    /// <summary>
    /// The match won by the first of the <paramref name="seats"/> whose pawn stands on the Finish, checked once the command
    /// has resolved in full (DEC-060): the player whose turn it is comes first, then the pawns their card pushed, in the
    /// order the pushes resolved (seat order). The match ends once, with every player still playing ranked.
    /// </summary>
    private static MatchRuleDecision? Won(RaceMatchState state, IEnumerable<int> seats, RaceRules rules, GameRoom match)
    {
        if (seats.FirstOrDefault(seat => state.Pawns.First(pawn => pawn.Seat == seat).Tile == rules.Finish.Index, -1) is var seat
            && seat < 0)
        {
            return null;
        }

        var won = state with { Winner = seat, Ended = RaceEnding.Finish };
        return MatchRuleDecision.Finish(JsonSerializer.SerializeToElement(won, Json), Standings(won, rules, match));
    }

    /// <summary>
    /// The seat's pawn moves <paramref name="steps"/> tiles, forward or back, stopping on the Finish or the Start rather
    /// than passing it (DEC-054, DEC-057), then lands there (<see cref="MoveTo"/>).
    /// </summary>
    private static (RaceMatchState State, Landing Landing) MoveBy(
        RaceMatchState state, int seat, int steps, RaceRules rules, IRandomSource random, GameRoom match)
    {
        var from = state.Pawns.First(pawn => pawn.Seat == seat).Tile;
        return MoveTo(state, seat, Math.Clamp(from + steps, rules.Start.Index, rules.Finish.Index), rules, random, match);
    }

    /// <summary>
    /// The seat's pawn lands on tile <paramref name="landed"/>, which resolves for its player: a Boost or Setback sends it
    /// on to its target, which never jumps again, and a Card tile where it ends, landed on or jumped to, draws its player a
    /// card (DEC-056, DEC-057).
    /// </summary>
    private static (RaceMatchState State, Landing Landing) MoveTo(
        RaceMatchState state, int seat, int landed, RaceRules rules, IRandomSource random, GameRoom match)
    {
        var from = state.Pawns.First(pawn => pawn.Seat == seat).Tile;
        var tile = rules.Tiles[landed];
        var to = tile.To ?? landed;
        var landing = new Landing(from, landed, to, tile.Type is RaceTileType.Normal or RaceTileType.Start ? null : tile.Type);
        var next = state with { Pawns = state.Pawns.Select(p => p.Seat == seat ? p with { Tile = to } : p).ToArray() };
        return rules.Tiles[to].Type is RaceTileType.Card ? DrawFor(next, seat, landing, rules, random, match) : (next, landing);
    }

    /// <summary>
    /// The mover draws the deck's top card (DEC-056), unless their hand is full, which leaves the deck as it is. An empty
    /// deck is first refilled: the discard pile and the hands of players who left are shuffled into a new one. With no
    /// card there either, as when every card is in a hand, nothing is drawn.
    /// </summary>
    private static (RaceMatchState State, Landing Landing) DrawFor(
        RaceMatchState state, int seat, Landing landing, RaceRules rules, IRandomSource random, GameRoom match)
    {
        if (HandOf(state, seat).Count >= rules.MaxHand)
        {
            return (state, landing with { Draw = RaceDraw.HandFull });
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
                return (state, landing with { Draw = RaceDraw.NoCards });
            }

            random.Shuffle(pool);
            deck = pool;
            discard = [];
            pawns = pawns.Select(p => playing.Contains(p.Seat) ? p : p with { Hand = null }).ToArray();
            reshuffled = true;
        }

        // The top card leaves the deck, so whoever saw it with Card Vision no longer sees the next one (DEC-059).
        var card = deck[0];
        pawns = pawns.Select(p => (p.Seat == seat ? p with { Hand = [.. p.Hand ?? [], card] } : p) with { Sees = null }).ToArray();
        return (
            state with { Pawns = pawns, Deck = deck.Skip(1).ToArray(), Discard = discard },
            landing with { Draw = RaceDraw.Drawn, Card = card, Reshuffled = reshuffled });
    }

    /// <summary>
    /// <c>PLAY_CARD</c> with <c>{ "card": "Pump" }</c>, and for Whale a <c>"target"</c> seat, plays one card from the
    /// player's hand onto the discard pile and ends the turn (DEC-057). Its effect uses the rules' card values: Pump moves
    /// the player forward, FOMO forward or back on one draw from the server, Airdrop rolls again, each resolving the tile
    /// landed on; Whale swaps tiles with the target, the player's new tile resolving for them (a Card tile draws), and
    /// Reverse moves every opponent back onto a tile whose Boost or Setback then sends them on, as a roll does. A pawn
    /// pushed back onto a Card tile draws there, for its own player (DEC-057, revised in STORY-055); the Whale's target,
    /// moved onto the player's old tile, draws nothing.
    /// The play is added to the turn's last move. Reaching the Finish wins. A card not named exactly
    /// (<c>invalid_card</c>), not in the hand (<c>card_not_held</c>), not playable on the holder's turn
    /// (<c>card_not_playable</c>) or a Whale without an opponent still playing as its target (<c>invalid_target</c>) is
    /// rejected, changing and drawing nothing.
    /// </summary>
    private static MatchRuleDecision PlayFor(
        RacePawn pawn, RaceMatchState state, RaceRules rules, JsonElement payload, IRandomSource random, GameRoom match)
    {
        if (state.LastMove is not { } move || move.Seat != pawn.Seat)
        {
            return MatchRuleDecision.Reject("game_state_unavailable");
        }

        if (CardIn(payload) is not { } card)
        {
            return MatchRuleDecision.Reject("invalid_card");
        }

        if (pawn.Hand?.Contains(card) != true)
        {
            return MatchRuleDecision.Reject("card_not_held");
        }

        if (!PlayableOnTurn(card))
        {
            return MatchRuleDecision.Reject("card_not_playable");
        }

        var playing = PlayingSeats(match);
        RacePawn? target = null;
        if (Targets(card)
            && (TargetIn(payload) is not { } seat
                || seat == pawn.Seat
                || !playing.Contains(seat)
                || (target = state.Pawns.FirstOrDefault(p => p.Seat == seat)) is null
                || (card is RaceCard.Rug && target.Tile <= rules.Start.Index)
                || (card is RaceCard.Liquidated && MemberAt(match, seat).SkipsNextTurn)))
        {
            return MatchRuleDecision.Reject("invalid_target");
        }

        var hand = pawn.Hand.ToList();
        hand.Remove(card);
        var next = state with
        {
            PlayPhase = null,
            Pawns = state.Pawns.Select(p => p.Seat == pawn.Seat ? p with { Hand = hand.Count == 0 ? null : hand } : p).ToArray(),
            Discard = [.. state.Discard ?? [], card],
        };
        next = CardPlayed(next, pawn.Seat);
        // A Shield blocks the attack at once and is spent; the target keeps their cards (DEC-059).
        if (target is { Shielded: true })
        {
            next = AttackBlocked(next, target.Seat);
            next = next with
            {
                Pawns = next.Pawns.Select(p => p.Seat == target.Seat ? p with { Shielded = null } : p).ToArray(),
                LastMove = move with { Play = new RacePlay(card, Target: target.Seat, Reply: RaceReply.Shielded) },
            };
            return MatchRuleDecision.Accept(JsonSerializer.SerializeToElement(next, Json), endsTurn: true);
        }

        // An attack on a player holding cards waits for their reply, which may be HODL (DEC-058).
        if (target is not null && HandOf(next, target.Seat).Count > 0)
        {
            next = next with
            {
                Reply = new RaceAttack(pawn.Seat, card, target.Seat),
                LastMove = move with { Play = new RacePlay(card, Target: target.Seat) },
            };
            return MatchRuleDecision.Accept(JsonSerializer.SerializeToElement(next, Json), awaitsReplyFrom: MemberAt(match, target.Seat).PlayerId);
        }

        var values = rules.CardValues;
        RacePlay play;
        Guid? skips = null;
        switch (card)
        {
            case RaceCard.Whale or RaceCard.Rug or RaceCard.Liquidated:
                (next, play, skips) = Attack(next, new RaceAttack(pawn.Seat, card, target!.Seat), rules, random, match);
                next = AttackLanded(next, pawn.Seat);
                break;

            case RaceCard.Reverse:
                var reversed = next.Pawns
                    .Where(p => p.Seat != pawn.Seat && playing.Contains(p.Seat) && p.Tile > rules.Start.Index)
                    .ToArray();
                (next, var pushed) = PushAll(next, reversed, values.Reverse, rules, random, match);
                play = new RacePlay(card, Pushed: pushed);
                next = pushed.Count > 0 ? AttackLanded(next, pawn.Seat) : next;
                break;

            default:
                int? roll = card is RaceCard.Airdrop ? random.Next(rules.MinMove, rules.MaxMove + 1) : null;
                var steps = roll
                    ?? (card is RaceCard.Pump ? values.Pump
                        : random.Next(0, 100) < values.FomoForwardChance ? values.FomoForward
                        : -values.FomoBack);
                (next, var landing) = MoveBy(next, pawn.Seat, steps, rules, random, match);
                play = new RacePlay(
                    card,
                    Steps: roll is null ? steps : null,
                    Roll: roll,
                    From: landing.From,
                    Landed: landing.Landed,
                    To: landing.To,
                    Effect: landing.Effect,
                    Draw: landing.Draw,
                    Drawn: landing.Card,
                    Reshuffled: landing.Reshuffled);
                break;
        }

        next = Arrived(state, next) with { LastMove = move with { Play = play } };
        return Won(next, [pawn.Seat, .. play.Pushed?.Select(push => push.Seat) ?? []], rules, match)
            ?? MatchRuleDecision.Accept(JsonSerializer.SerializeToElement(next, Json), endsTurn: true, skipsNextTurnOf: skips);
    }

    /// <summary>
    /// The target's reply to the attack the turn waits for (DEC-058): <c>PLAY_CARD</c> with HODL blocks it, both cards
    /// going to the discard pile, and <c>PASS</c> lets it land. Either ends the attacker's turn. Any other card is
    /// <c>card_not_playable</c>, HODL not in the hand <c>card_not_held</c>, and <c>ROLL</c> or <c>END_TURN</c>
    /// <c>reply_pending</c>; none changes anything.
    /// </summary>
    private static MatchRuleDecision ReplyFor(
        RacePawn pawn, RaceAttack attack, RaceMatchState state, RaceRules rules, MatchCommandContext context)
    {
        if (state.LastMove is not { Play: { } pending } move || move.Seat != attack.Seat)
        {
            return MatchRuleDecision.Reject("game_state_unavailable");
        }

        if (context.Type == Pass)
        {
            var (landed, play, skips) = Attack(state with { Reply = null }, attack, rules, context.Random, context.Match);
            landed = AttackLanded(Arrived(state, landed), attack.Seat) with { LastMove = move with { Play = play with { Reply = RaceReply.Allowed } } };
            return Won(landed, [attack.Seat, attack.Target], rules, context.Match)
                ?? MatchRuleDecision.Accept(JsonSerializer.SerializeToElement(landed, Json), endsTurn: true, skipsNextTurnOf: skips);
        }

        if (context.Type != PlayCard)
        {
            return MatchRuleDecision.Reject(ReplyPending);
        }

        if (CardIn(context.Payload) is not { } card)
        {
            return MatchRuleDecision.Reject("invalid_card");
        }

        if (card != RaceCard.Hodl)
        {
            return MatchRuleDecision.Reject("card_not_playable");
        }

        if (pawn.Hand?.Contains(card) != true)
        {
            return MatchRuleDecision.Reject("card_not_held");
        }

        var hand = pawn.Hand.ToList();
        hand.Remove(card);
        var blocked = state with
        {
            Reply = null,
            Pawns = state.Pawns.Select(p => p.Seat == pawn.Seat ? p with { Hand = hand.Count == 0 ? null : hand } : p).ToArray(),
            Discard = [.. state.Discard ?? [], card],
            LastMove = move with { Play = pending with { Reply = RaceReply.Blocked } },
        };
        blocked = AttackBlocked(CardPlayed(blocked, pawn.Seat), pawn.Seat);
        return MatchRuleDecision.Accept(JsonSerializer.SerializeToElement(blocked, Json), endsTurn: true);
    }

    /// <summary>
    /// What an attack that lands does (DEC-058): Rug moves the target back the rules' tiles, following the Boost or
    /// Setback it lands on and drawing on a Card tile, as Reverse does; Liquidated makes the target skip their next turn; Whale
    /// swaps the attacker's and the target's tiles, the attacker's new tile resolving for them (a Card tile draws). Returns
    /// the state, the play and the player who skips a turn, if any.
    /// </summary>
    private static (RaceMatchState State, RacePlay Play, Guid? Skips) Attack(
        RaceMatchState state, RaceAttack attack, RaceRules rules, IRandomSource random, GameRoom match)
    {
        var attacker = state.Pawns.First(p => p.Seat == attack.Seat);
        var target = state.Pawns.First(p => p.Seat == attack.Target);
        switch (attack.Card)
        {
            case RaceCard.Rug:
                var (rugged, pushed) = PushAll(state, [target], rules.CardValues.Rug, rules, random, match);
                return (rugged, new RacePlay(attack.Card, Target: target.Seat, Pushed: pushed), null);

            case RaceCard.Liquidated:
                return (state, new RacePlay(attack.Card, Target: target.Seat), MemberAt(match, target.Seat).PlayerId);

            default:
                var next = state with { Pawns = state.Pawns.Select(p => p.Seat == target.Seat ? p with { Tile = attacker.Tile } : p).ToArray() };
                (next, var swapped) = MoveTo(next, attacker.Seat, target.Tile, rules, random, match);
                var play = new RacePlay(
                    attack.Card,
                    From: swapped.From,
                    To: swapped.To,
                    Effect: swapped.Effect,
                    Draw: swapped.Draw,
                    Drawn: swapped.Card,
                    Reshuffled: swapped.Reshuffled,
                    Target: target.Seat);
                return (next, play, null);
        }
    }

    /// <summary>
    /// The <paramref name="pawns"/> another player's card moves back <paramref name="tiles"/>, in seat order, each landing
    /// where <see cref="PushBack"/> says. A pawn that ends on a Card tile draws there, for its own player, as any landing
    /// does (DEC-057, revised in STORY-055); the card is shown only to them.
    /// </summary>
    private static (RaceMatchState State, IReadOnlyList<RacePush> Pushed) PushAll(
        RaceMatchState state, IReadOnlyList<RacePawn> pawns, int tiles, RaceRules rules, IRandomSource random, GameRoom match)
    {
        var pushed = new List<RacePush>();
        foreach (var pawn in pawns)
        {
            var push = PushBack(pawn, tiles, rules);
            state = Pushed(state, [push]);
            if (rules.Tiles[push.To].Type is RaceTileType.Card)
            {
                (state, var drawn) = DrawFor(state, push.Seat, new Landing(push.From, push.Landed, push.To, push.Effect), rules, random, match);
                push = push with { Draw = drawn.Draw, Card = drawn.Card, Reshuffled = drawn.Reshuffled };
            }

            pushed.Add(push);
        }

        return (state, pushed);
    }

    /// <summary>
    /// Where a pawn another player's card moves back <paramref name="tiles"/> ends: no further back than the Start, then on
    /// with the Boost or Setback it lands on (DEC-057).
    /// </summary>
    private static RacePush PushBack(RacePawn pawn, int tiles, RaceRules rules) =>
        Math.Max(pawn.Tile - tiles, rules.Start.Index) is var landed && rules.Tiles[landed] is { Jumps: true } jump
            ? new RacePush(pawn.Seat, pawn.Tile, landed, jump.To!.Value, jump.Type)
            : new RacePush(pawn.Seat, pawn.Tile, landed, landed);

    private static RaceMatchState Pushed(RaceMatchState state, IReadOnlyList<RacePush> pushed) => state with
    {
        Pawns = state.Pawns.Select(p => pushed.FirstOrDefault(push => push.Seat == p.Seat) is { } push ? p with { Tile = push.To } : p).ToArray(),
    };

    /// <summary>The cards played on one opponent the player picks: Whale, Rug and Liquidated, which HODL can block (DEC-058).</summary>
    private static bool Targets(RaceCard card) => card is RaceCard.Whale or RaceCard.Rug or RaceCard.Liquidated;

    /// <summary>
    /// The attack the turn waits on, while the engine still waits for its target's reply from the attacker's turn; null
    /// otherwise, as when the target or the attacker left and the engine moved on (DEC-058).
    /// </summary>
    private static RaceAttack? LiveReply(RaceMatchState state, GameRoom match) =>
        state.Reply is { } attack
        && match.ReplyingPlayerId is { } replying
        && SeatOf(match, replying) == attack.Target
        && SeatOf(match, match.CurrentPlayerId) == attack.Seat
            ? attack
            : null;

    private static RoomMember MemberAt(GameRoom match, int seat) => match.Members.Single(member => member.Seat == seat);

    /// <summary>The card the payload names, by its exact name; a number or any other name is none.</summary>
    private static RaceCard? CardIn(JsonElement payload) =>
        payload.ValueKind == JsonValueKind.Object
        && payload.TryGetProperty("card", out var card)
        && card.ValueKind == JsonValueKind.String
            ? Enum.GetValues<RaceCard>().Cast<RaceCard?>().FirstOrDefault(kind => kind.ToString() == card.GetString())
            : null;

    private static int? TargetIn(JsonElement payload) =>
        payload.ValueKind == JsonValueKind.Object
        && payload.TryGetProperty("target", out var target)
        && target.ValueKind == JsonValueKind.Number
        && target.TryGetInt32(out var seat)
            ? seat
            : null;

    /// <summary>
    /// Whether the seat holds a card it can play on its own turn, which opens the Play phase (DEC-055, DEC-056); a card
    /// played only in reply to another player, like HODL, never does.
    /// </summary>
    private static bool HoldsCard(RaceMatchState state, int seat) => HandOf(state, seat).Any(PlayableOnTurn);

    /// <summary>
    /// Whether a card can be played on its holder's turn: every card but HODL, which is played only in reply to an attack
    /// (STORY-053, DEC-057; STORY-054, DEC-058).
    /// </summary>
    private static bool PlayableOnTurn(RaceCard card) => card is not RaceCard.Hodl;

    private static IReadOnlyList<RaceCard> HandOf(RaceMatchState state, int seat) =>
        state.Pawns.FirstOrDefault(pawn => pawn.Seat == seat)?.Hand ?? [];

    private static HashSet<int> PlayingSeats(GameRoom match) =>
        match.Members
            .Where(member => member is { HasLeft: false, Seat: not null })
            .Select(member => member.Seat!.Value)
            .ToHashSet();

    /// <summary>
    /// The attack's target didn't reply in time (DEC-058): the attack lands as if they had passed, and their HODL, if
    /// they hold one, stays in their hand. A Rug that pushes its target onto a Boost to the Finish wins the race for
    /// them. A state this server can't play, or no live attack, changes nothing.
    /// </summary>
    public MatchRuleDecision? TimeOutReply(MatchTurnContext context)
    {
        if (context.State is not { } json
            || Read(json) is not { Winner: null } state
            || !_rulesByVersion.TryGetValue(state.RulesVersion, out var rules)
            || !IsPlayable(state, rules)
            || LiveReply(state, context.Match) is not { } attack
            || state.LastMove is not { Play: not null } move
            || move.Seat != attack.Seat)
        {
            return null;
        }

        var (landed, play, skips) = Attack(state with { Reply = null }, attack, rules, context.Random, context.Match);
        landed = AttackLanded(Arrived(state, landed), attack.Seat) with { LastMove = move with { Play = play with { Reply = RaceReply.TimedOut } } };
        return Won(landed, [attack.Seat, attack.Target], rules, context.Match)
            ?? MatchRuleDecision.Accept(JsonSerializer.SerializeToElement(landed, Json), skipsNextTurnOf: skips);
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
    /// The track, the pawns with how many cards each holds, the last move with any card played, the current player's
    /// phase, the sizes of the deck and discard pile, the card values and, once the race is won, the winner's seat, the
    /// same for every player. Only the viewer's own hand, which of its cards they can play on their turn, and the cards
    /// they last drew are shown by name (DEC-056, DEC-057); the deck's order is never shown, except its top card to a player
    /// who used Card Vision on it, and a stolen card only to the two players (DEC-059). Everyone sees armed Speed Boosts,
    /// raised Shields and the latest ability used. Pawns of
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
        var reply = context.Match.Status == RoomStatus.Active ? LiveReply(state, context.Match) : null;
        RacePhase? phase = context.Match.Status != RoomStatus.Active ? null
            : reply is not null ? RacePhase.Reply
            : state.PlayPhase is { } seat && seat == SeatOf(context.Match, context.Match.CurrentPlayerId) ? RacePhase.Play
            : RacePhase.Move;
        var skipping = context.Match.Status != RoomStatus.Active ? []
            : context.Match.Members
                .Where(member => member is { SkipsNextTurn: true, Seat: not null } && playing.Contains(member.Seat!.Value))
                .Select(member => member.Seat!.Value)
                .Order()
                .ToArray();
        var view = new RaceBoardView(
            rules.Version,
            rules.Tiles.Select(tile => new RaceTileView(tile.Index, tile.Type, tile.To)).ToArray(),
            state.Pawns
                .Where(pawn => playing.Contains(pawn.Seat))
                .OrderBy(pawn => pawn.Seat)
                .Select(pawn => new RacePawnView(pawn.Seat, pawn.Tile, pawn.Hand?.Count ?? 0, pawn.Boost, pawn.Shielded))
                .ToArray(),
            state.LastMove is { } move ? ForViewer(move, viewer) : null,
            phase,
            state.Winner,
            rules.MaxHand,
            state.Deck?.Count ?? 0,
            state.Discard?.Count ?? 0,
            viewer is { } mine ? HandOf(state, mine) : null,
            viewer is { } holder ? HandOf(state, holder).Where(PlayableOnTurn).Distinct().ToArray() : null,
            rules.CardValues,
            reply,
            skipping.Length > 0 ? skipping : null,
            viewer is { } seer && state.Pawns.First(pawn => pawn.Seat == seer).Sees == true && state.Deck is [var top, ..] ? top : null,
            state.Ability is { } used && used.Seat != viewer && used.Target != viewer ? used with { Card = null } : state.Ability,
            state.Ended);
        return JsonSerializer.SerializeToElement(view, Json);
    }

    /// <summary>
    /// The last move as <paramref name="viewer"/> sees it: a card drawn is named only to the player who drew it, the mover
    /// for their own moves and each pushed pawn's player for theirs (DEC-056, DEC-057).
    /// </summary>
    private static RaceMove ForViewer(RaceMove move, int? viewer)
    {
        var play = move.Play is { } played
            ? played with
            {
                Drawn = move.Seat == viewer ? played.Drawn : null,
                Pushed = played.Pushed?.Select(push => push.Seat == viewer ? push : push with { Card = null }).ToArray(),
            }
            : null;
        return move.Seat == viewer ? move with { Play = play } : move with { Card = null, Play = play };
    }

    private static int? SeatOf(GameRoom match, Guid? playerId) =>
        match.Members.SingleOrDefault(member => member.PlayerId == playerId)?.Seat;

    /// <summary>
    /// Every pawn stands on the track, once per seat, holding no more than a full hand of known cards, with a Speed Boost of
    /// a card move's size if armed, moved last by a command the match counted and with no negative statistics.
    /// </summary>
    private static bool IsPlayable(RaceMatchState state, RaceRules rules) =>
        state.Pawns.All(pawn =>
            pawn.Tile >= 0
            && pawn.Tile < rules.Tiles.Count
            && (pawn.Hand?.Count ?? 0) <= rules.MaxHand
            && pawn.Boost is null or (>= 1 and <= RaceRules.CardMoveLimit)
            && (pawn.Since is null || (pawn.Since >= 1 && pawn.Since <= (state.Moves ?? 0)))
            && pawn.Stats?.IsValid() != false)
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
    /// neither and draws nothing. The attack awaiting a reply (DEC-058) and the latest ability used (DEC-059). How many
    /// commands have moved a pawn, and once the match is over how it ended (DEC-060).
    /// </summary>
    private sealed record RaceMatchState(
        int RulesVersion,
        IReadOnlyList<RacePawn> Pawns,
        RaceMove? LastMove = null,
        int? PlayPhase = null,
        int? Winner = null,
        IReadOnlyList<RaceCard>? Deck = null,
        IReadOnlyList<RaceCard>? Discard = null,
        RaceAttack? Reply = null,
        RaceAbilityUse? Ability = null,
        int? Moves = null,
        RaceEnding? Ended = null);

    /// <summary>
    /// An attack waiting for its target's reply (DEC-058): the attacker's seat, the card, already on the discard pile, and
    /// the target's seat. Everyone sees it.
    /// </summary>
    private sealed record RaceAttack(int Seat, RaceCard Card, int Target);

    /// <summary>How an attack's target answered it (DEC-058).</summary>
    private enum RaceReply
    {
        /// <summary>They played HODL; the attack did nothing.</summary>
        Blocked,

        /// <summary>They let it land.</summary>
        Allowed,

        /// <summary>They didn't reply in time; it landed.</summary>
        TimedOut,

        /// <summary>Their Shield blocked it, with no reply window (DEC-059).</summary>
        Shielded,
    }

    /// <summary>
    /// A seat's pawn and its hand, null while empty, and its abilities until they act (DEC-059): the tiles Speed Boost adds
    /// to the next roll, a raised Shield, and whether its player sees the deck's top card with Card Vision. The command that
    /// last moved it, counted in <see cref="RaceMatchState.Moves"/> (null if it never moved), and its player's statistics
    /// (DEC-060).
    /// </summary>
    private sealed record RacePawn(
        int Seat,
        int Tile,
        IReadOnlyList<RaceCard>? Hand = null,
        int? Boost = null,
        bool? Shielded = null,
        bool? Sees = null,
        int? Since = null,
        RaceStats? Stats = null);

    /// <summary>A pawn as every player sees it: how many cards it holds, not which, and its armed Speed Boost or Shield.</summary>
    private sealed record RacePawnView(int Seat, int Tile, int Cards, int? Boost, bool? Shielded);

    /// <summary>
    /// A seat's move: the roll, the tile it left, the tile it <paramref name="Landed"/> on and the tile it ended
    /// <paramref name="To"/>, after a Boost or Setback sent it on, and the <paramref name="Effect"/> of the tile it landed
    /// on (null for a Normal tile). On a Card tile, the <paramref name="Draw"/>, the <paramref name="Card"/> drawn, shown
    /// only to the seat that drew it, and whether the deck was <paramref name="Reshuffled"/> first. Then the card the seat
    /// <paramref name="Play"/>ed, if any (DEC-057).
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
        bool? Reshuffled = null,
        RacePlay? Play = null)
    {
        /// <summary>The tiles Speed Boost added to the roll (DEC-059).</summary>
        public int? Boost { get; init; }
    }

    /// <summary>Where a pawn's own move took it and what the tile did; part of a roll or a card's move.</summary>
    private sealed record Landing(
        int From, int Landed, int To, RaceTileType? Effect, RaceDraw? Draw = null, RaceCard? Card = null, bool? Reshuffled = null);

    /// <summary>
    /// A card played (DEC-057). Pump and FOMO show the signed <paramref name="Steps"/> moved, Airdrop the
    /// <paramref name="Roll"/>; all three where the player went, as a roll does, with the card <paramref name="Drawn"/>
    /// shown only to them. Whale shows the tiles the player went <paramref name="From"/> and <paramref name="To"/> and the
    /// <paramref name="Target"/> seat, which went the other way, and what the player's new tile did; Reverse every
    /// opponent it <paramref name="Pushed"/> back.
    /// </summary>
    private sealed record RacePlay(
        RaceCard Card,
        int? Steps = null,
        int? Roll = null,
        int? From = null,
        int? Landed = null,
        int? To = null,
        RaceTileType? Effect = null,
        RaceDraw? Draw = null,
        RaceCard? Drawn = null,
        bool? Reshuffled = null,
        int? Target = null,
        IReadOnlyList<RacePush>? Pushed = null,
        RaceReply? Reply = null);

    /// <summary>
    /// An opponent's pawn moved back by Reverse or Rug: the tile it <paramref name="Landed"/> on and, after a Boost or
    /// Setback there (the <paramref name="Effect"/>, null otherwise) sent it on, the tile it ended <paramref name="To"/>
    /// (DEC-057). On a Card tile, the <paramref name="Draw"/>, the <paramref name="Card"/> drawn, shown only to that
    /// pawn's player, and whether the deck was <paramref name="Reshuffled"/> first (revised in STORY-055).
    /// </summary>
    private sealed record RacePush(
        int Seat, int From, int Landed, int To, RaceTileType? Effect = null, RaceDraw? Draw = null, RaceCard? Card = null, bool? Reshuffled = null);

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

    /// <summary>A turn's phases (RACE-004, DEC-055): Reply while it waits for an attack's target (DEC-058).</summary>
    private enum RacePhase
    {
        Move,
        Play,
        Reply,
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
        IReadOnlyList<RaceCard>? Hand,
        IReadOnlyList<RaceCard>? Playable,
        RaceCardValues CardValues,
        RaceAttack? Reply,
        IReadOnlyList<int>? Skipping,
        RaceCard? Next,
        RaceAbilityUse? Ability,
        RaceEnding? Ended);

    private sealed record RaceTileView(int Index, RaceTileType Type, int? To);
}
