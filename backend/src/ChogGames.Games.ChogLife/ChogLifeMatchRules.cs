using System.Text.Json;
using System.Text.Json.Serialization;
using ChogGames.Core.Games;
using ChogGames.Core.Matches;
using ChogGames.Core.Rooms;
using ChogGames.Games.ChogLife.Abilities;
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
/// that runs out is skipped. The lane taken holds a stake of the player's liquid MON, an asset that pays out when the pawn
/// rejoins the main track, a loss bigger than the liquid MON held becomes a liability, and every player sees each
/// player's liquid MON, assets, bonuses, liabilities and total portfolio (STORY-040, DEC-042). A pawn reaching the Finish
/// finishes its player's game (STORY-041, DEC-043): the first to arrive gets the finish bonus, and finished players take
/// no more turns. The match ends when every player still playing has finished, or when its time cap is up, and players
/// are ranked by portfolio, then by who finished first, then by who is further along. Players use the ability they
/// locked at start (STORY-042, DEC-044): Reward Boost, Loss Protection and Lucky Reroll are armed before a roll and act
/// on the first outcome they fit; Event Preview, armed at a fork, shows only its player their next move on the lane they
/// choose, which they then keep or switch blind. Holders may also lock Double Dice, whose roll moves by the die they keep
/// of those rolled, or Tax the Rich, which takes MON from the richest opponent (STORY-091, DEC-078).
/// </summary>
public sealed partial class ChogLifeMatchRules : IMatchRules
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

    /// <summary>The time cap of the rules version new matches start with (ENG-011, DEC-037).</summary>
    public TimeSpan? TimeLimit => _current.TimeCap;

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
    /// unless the pawn stopped on a fork: then the player chooses a lane first (DEC-041). A pawn that reaches the Finish
    /// finishes its player's game, and the match ends once every player still playing has (STORY-041, DEC-043). A pawn
    /// already on the Finish, from a state before STORY-041, doesn't move and draws nothing; it finishes there. A match
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
        if (state.Pawns.FirstOrDefault(pawn => pawn.Seat == seat) is not { } pawn || !IsPlayable(pawn, rules))
        {
            return MatchRuleDecision.Reject("game_state_unavailable");
        }

        // A pending choice belongs to the turn it was made in; one left by a player who has since gone counts for nobody.
        var choosing = state.Choosing == pawn.Seat;
        var confirming = state.Confirming == pawn.Seat;
        state = state with { Choosing = null, Confirming = null };
        return context.Type == Roll
            ? RollFor(pawn, choosing || confirming, state, rules, context.Payload, context.Random, context.Match)
            : ChooseFor(pawn, choosing, confirming, state, rules, context.Payload, context.Random);
    }

    /// <summary>
    /// The pawn stands on the board with a portfolio in range, a lane it can take, a stake on a lane its rules pay out, an
    /// armed ability this game knows (Event Preview only on a fork before its lane is chosen) and, only on a fork with its
    /// lane chosen, a preview's draws in range (STORY-042, DEC-044).
    /// </summary>
    private static bool IsPlayable(LifePawn pawn, LifeRules rules) =>
        pawn.Tile >= 0
        && pawn.Tile < rules.Tiles.Count
        && pawn.Mon is not null
        && PortfolioOf(pawn).IsValid
        && pawn.Lane is (null or LifeLane.Safe or LifeLane.Degen)
        && (pawn.Stake is null || (pawn.Stake.Amount > 0 && rules.Stakes.ContainsKey(pawn.Stake.Lane)))
        && (pawn.Armed is null || (pawn.Armed.IsValid()
            && (pawn.Armed.Ability != LifeAbilities.EventPreview || (rules.Tiles[pawn.Tile].Type == LifeTileType.Branch && pawn.Lane is null))))
        && (pawn.Preview is null || IsPreviewValid(pawn, pawn.Preview, rules))
        && AreDiceValid(pawn, rules)
        && (pawn.Stats is null || pawn.Stats.IsValid());

    private static MatchRuleDecision RollFor(
        LifePawn pawn, bool choosing, LifeMatchState state, LifeRules rules, JsonElement payload, IRandomSource random, GameRoom match)
    {
        if (choosing)
        {
            return MatchRuleDecision.Reject("choose_lane_first");
        }

        // After Double Dice the roll moves by the die its player keeps, or the one kept for them when a turn ran out.
        int? die = null;
        if (pawn.Dice is { } dice)
        {
            die = pawn.Kept ?? DieIn(payload);
            if (die is null)
            {
                return MatchRuleDecision.Reject("choose_die");
            }

            if (!dice.Contains(die.Value))
            {
                return MatchRuleDecision.Reject("invalid_die");
            }
        }

        var next = Moved(pawn, state, rules, random, die);
        var move = next.LastMove!;
        if (move.To != rules.Finish.Index)
        {
            var atFork = next.Choosing is not null;
            return MatchRuleDecision.Accept(JsonSerializer.SerializeToElement(next, Json), endsTurn: !atFork);
        }

        if (PlayingSeats(match).All(next.Finished!.Contains))
        {
            var ended = next with { Ended = LifeEnding.Finish };
            return MatchRuleDecision.Finish(JsonSerializer.SerializeToElement(ended, Json), Standings(ended, rules, match));
        }

        return MatchRuleDecision.Accept(JsonSerializer.SerializeToElement(next, Json), finished: true);
    }

    /// <summary>
    /// The state after <paramref name="pawn"/> rolls: the die, its stake's payout if it rejoins the main track, then the
    /// tile it stops on, with its armed ability applied to that tile's outcome if it fits (STORY-042, DEC-044). A roll its
    /// player previewed plays out with the draws already shown to them, and one after Double Dice moves by the
    /// <paramref name="die"/> kept, drawing no die (STORY-091, DEC-078). A Tax the Rich its player used this turn goes with
    /// the move. Reaching the Finish records the player's place.
    /// </summary>
    private static LifeMatchState Moved(LifePawn pawn, LifeMatchState state, LifeRules rules, IRandomSource random, int? die = null)
    {
        if (pawn.Preview is { } drawn)
        {
            random = new PreviewDraws(drawn, random);
        }

        LifeMove move;
        var portfolio = PortfolioOf(pawn);
        var stake = pawn.Stake;
        var armed = pawn.Armed;
        if (pawn.Tile == rules.Finish.Index)
        {
            move = new LifeMove(pawn.Seat, null, pawn.Tile, pawn.Tile, []);
        }
        else
        {
            var roll = die ?? random.Next(1, rules.DieFaces + 1);
            var path = rules.Path(pawn.Tile, roll, pawn.Lane);

            // A stake pays out as the pawn rejoins the main track, before the tile it stops on resolves (DEC-042).
            LifeMaturity? matured = null;
            if (stake is not null && path.Any(tile => rules.Tiles[tile].Lane == LifeLane.Main))
            {
                var payout = rules.Payout(stake, random);
                portfolio = (portfolio with { Assets = 0 }).Settle(payout);
                matured = new LifeMaturity(stake.Lane, stake.Amount, payout);
                stake = null;
            }

            var resolved = Resolve(rules, path[^1], portfolio.Liquid, armed, random);
            int? change = null;
            if (resolved.Change is { } wanted)
            {
                var settled = portfolio.Settle(wanted);
                change = settled.Total - portfolio.Total;
                portfolio = settled;
            }

            if (resolved.Boost is { } boost)
            {
                portfolio = portfolio with { Bonuses = (int)Math.Min((long)portfolio.Bonuses + boost, LifeRules.MaxMon) };
            }

            armed = resolved.Armed;
            move = new LifeMove(
                pawn.Seat, roll, pawn.Tile, path[^1], path, change, Matured: matured,
                Boost: resolved.Boost, Saved: resolved.Saved, Rerolled: resolved.Rerolled, Draws: resolved.Draws,
                Dice: pawn.Dice);
        }

        if (state.Tax is { } tax && tax.Seat == pawn.Seat)
        {
            move = move with { Taxed = tax };
        }

        var finished = state.Finished ?? [];
        if (move.To == rules.Finish.Index && !finished.Contains(pawn.Seat))
        {
            // Arriving finishes the player's game; only the first to arrive gets the bonus (LIFE-007, DEC-043).
            var place = finished.Count + 1;
            var bonus = place == 1 ? rules.FinishBonus : 0;
            portfolio = portfolio with { Bonuses = (int)Math.Min((long)portfolio.Bonuses + bonus, LifeRules.MaxMon) };
            finished = [.. finished, pawn.Seat];
            move = move with { Place = place, Bonus = bonus > 0 ? bonus : null };
        }

        var atFork = rules.Tiles[move.To].Type == LifeTileType.Branch;
        var gained = portfolio.Total - PortfolioOf(pawn).Total;
        var moved = pawn with
        {
            Tile = move.To, Lane = null, Stake = stake, Armed = armed, Preview = null, Dice = null, Kept = null,
            Stats = Tallied(pawn.Stats, move, gained, rules),
        };
        return state with
        {
            Pawns = Replace(state, Holding(moved, portfolio)),
            LastMove = move,
            Choosing = atFork ? pawn.Seat : null,
            Finished = finished.Count > 0 ? finished : null,
            Tax = null,
        };
    }

    /// <summary>
    /// The match ends (STORY-041, DEC-043): its time cap is up, or every player still playing has finished. A lane
    /// choice still pending is dropped, and a stake still held counts at what was staked, as an asset. Players are ranked
    /// by <see cref="Standings"/>. A state this server can't play ranks everyone still playing first, together, so the
    /// match still ends with one result.
    /// </summary>
    public MatchEnding EndMatch(MatchEndContext context)
    {
        var stored = context.State is { } json ? Read(json) : StartOf(context.Match, _current);
        var ending = context.TimeUp ? LifeEnding.TimeUp : LifeEnding.Finish;
        if (stored is null || !_rulesByVersion.TryGetValue(stored.RulesVersion, out var rules)
            || !WithMon(stored, rules).Pawns.All(pawn => IsPlayable(pawn, rules))
            || stored.Pawns.DistinctBy(pawn => pawn.Seat).Count() != stored.Pawns.Count)
        {
            var together = PlayerSeats(context.Match).Select(seat => new MatchStanding(seat.PlayerId, 1)).ToArray();
            var unchanged = context.State is { ValueKind: JsonValueKind.Object } state
                ? state.Clone()
                : JsonSerializer.SerializeToElement(new { ended = ending.ToString() });
            return new MatchEnding(unchanged, together);
        }

        var ended = stored with { Choosing = null, Confirming = null, Ended = ending };
        return new MatchEnding(JsonSerializer.SerializeToElement(ended, Json), Standings(ended, rules, context.Match));
    }

    /// <summary>
    /// A rank for every player still playing (LIFE-007, DEC-043): the highest total portfolio first; on a tie, whoever
    /// reached the Finish first, any finished player before those still on the board; then whoever has fewer tiles left
    /// to the Finish. Players still level share the rank (1, 1, 3). Each comes with the player's result (STORY-043,
    /// DEC-046), which the engine stores with the rank.
    /// </summary>
    private static IReadOnlyList<MatchStanding> Standings(LifeMatchState state, LifeRules rules, GameRoom match)
    {
        var finished = state.Finished ?? [];
        var pawns = WithMon(state, rules).Pawns.ToDictionary(pawn => pawn.Seat);
        var scored = PlayerSeats(match)
            .Select(player => (player.PlayerId, player.Seat, Score: pawns.TryGetValue(player.Seat, out var pawn)
                ? (Total: PortfolioOf(pawn).Total,
                    Place: PlaceOf(finished, pawn.Seat) ?? int.MaxValue,
                    Left: rules.StepsToFinish(pawn.Tile))
                : (Total: int.MinValue, Place: int.MaxValue, Left: int.MaxValue)))
            .ToArray();
        return scored
            .Select(player => new MatchStanding(
                player.PlayerId,
                1 + scored.Count(other => Beats(other.Score, player.Score)),
                pawns.TryGetValue(player.Seat, out var pawn)
                    ? JsonSerializer.SerializeToElement(ResultOf(pawn, PlaceOf(finished, pawn.Seat), rules), Json)
                    : null))
            .ToArray();
    }

    private static bool Beats((int Total, int Place, int Left) a, (int Total, int Place, int Left) b) =>
        a.Total != b.Total ? a.Total > b.Total
        : a.Place != b.Place ? a.Place < b.Place
        : a.Left < b.Left;

    /// <summary>The order in which <paramref name="seat"/> reached the Finish, from 1; null while still on the board.</summary>
    private static int? PlaceOf(IReadOnlyList<int> finished, int seat)
    {
        for (var place = 0; place < finished.Count; place++)
        {
            if (finished[place] == seat)
            {
                return place + 1;
            }
        }

        return null;
    }

    /// <summary>The seats of the players still playing.</summary>
    private static IEnumerable<int> PlayingSeats(GameRoom match) => PlayerSeats(match).Select(player => player.Seat);

    private static IEnumerable<(Guid PlayerId, int Seat)> PlayerSeats(GameRoom match) => match.Members
        .Where(member => member is { HasLeft: false, Seat: not null })
        .Select(member => (member.PlayerId, member.Seat!.Value));

    /// <summary>
    /// Only the two lanes the fork offers are accepted, by name as the board shows them (<c>Safe</c>, <c>Degen</c>);
    /// anything else, or a choice with none pending, changes nothing. With Event Preview armed, the choice draws the
    /// next move on that lane and keeps the turn for the player to keep it or switch (STORY-042, DEC-044): a second
    /// <c>CHOOSE_LANE</c> naming the same lane keeps it, the other lane switches; either ends the turn.
    /// </summary>
    private static MatchRuleDecision ChooseFor(
        LifePawn pawn, bool choosing, bool confirming, LifeMatchState state, LifeRules rules, JsonElement payload, IRandomSource random)
    {
        if ((!choosing && !confirming) || rules.Tiles[pawn.Tile].Type != LifeTileType.Branch)
        {
            return MatchRuleDecision.Reject("no_choice_pending");
        }

        if (LaneIn(payload) is not { } lane)
        {
            return MatchRuleDecision.Reject("invalid_lane");
        }

        if (confirming)
        {
            return MatchRuleDecision.Accept(JsonSerializer.SerializeToElement(KeepOrSwitch(state, pawn, lane), Json), endsTurn: true);
        }

        var chosen = Choose(state, pawn, lane, byDefault: false, rules);
        if (pawn.Armed?.Ability != LifeAbilities.EventPreview)
        {
            return MatchRuleDecision.Accept(JsonSerializer.SerializeToElement(chosen, Json), endsTurn: true);
        }

        var previewed = Previewed(chosen, pawn.Seat, rules, random) with { Confirming = pawn.Seat };
        return MatchRuleDecision.Accept(JsonSerializer.SerializeToElement(previewed, Json), endsTurn: false);
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

    /// <summary>
    /// The pawn keeps its place on the fork with the lane it will leave by, staking the rules' fork stake of its liquid
    /// MON on it, or all it holds if less (DEC-042); a pawn already holding a stake stakes nothing more. The last move
    /// says which lane and what was staked.
    /// </summary>
    private static LifeMatchState Choose(
        LifeMatchState state, LifePawn pawn, LifeLane lane, bool byDefault, LifeRules rules)
    {
        var staked = pawn.Stake is null ? Math.Min(rules.ForkStake, pawn.Mon!.Value) : 0;
        var chosen = staked > 0
            ? pawn with { Lane = lane, Mon = pawn.Mon - staked, Stake = new LifeAsset(lane, staked) }
            : pawn with { Lane = lane };
        return state with
        {
            Pawns = Replace(state, chosen),
            LastMove = state.LastMove is { } move && move.Seat == pawn.Seat
                ? move with { Lane = lane, ByDefault = byDefault ? true : null, Staked = staked > 0 ? staked : null }
                : state.LastMove,
            Choosing = null,
        };
    }

    private static LifePortfolio PortfolioOf(LifePawn pawn) =>
        new(pawn.Mon ?? 0, pawn.Stake?.Amount ?? 0, pawn.Bonuses ?? 0, pawn.Liabilities ?? 0);

    /// <summary>The pawn holding <paramref name="portfolio"/>'s liquid MON, bonuses and liabilities; zeros aren't stored.</summary>
    private static LifePawn Holding(LifePawn pawn, LifePortfolio portfolio) => pawn with
    {
        Mon = portfolio.Liquid,
        Bonuses = portfolio.Bonuses == 0 ? null : portfolio.Bonuses,
        Liabilities = portfolio.Liabilities == 0 ? null : portfolio.Liabilities,
    };

    private static LifePawn[] Replace(LifeMatchState state, LifePawn pawn) =>
        state.Pawns.Select(p => p.Seat == pawn.Seat ? pawn : p).ToArray();

    /// <summary>
    /// A turn that runs out while its player is choosing a lane takes the rules' default lane (DEC-041), drawing an armed
    /// Event Preview's move on it; one that runs out while they decide whether to keep a previewed lane keeps it
    /// (STORY-042, DEC-044). One that runs out after Double Dice, before a die was kept, keeps the highest for the player's
    /// next roll (STORY-091, DEC-078). Any other turn is skipped with the state unchanged, and so is a state this server
    /// can't play.
    /// </summary>
    public JsonElement? TimeOutTurn(MatchTurnContext context)
    {
        if (context.State is { } dicing && Read(dicing) is { } rolled && DiceKept(rolled, context.Match) is { } kept)
        {
            return JsonSerializer.SerializeToElement(kept, Json);
        }

        if (context.State is not { } json
            || Read(json) is not { } read
            || (read.Choosing ?? read.Confirming) is not { } seat
            || read with { Choosing = null, Confirming = null } is not { } stored
            || !_rulesByVersion.TryGetValue(stored.RulesVersion, out var rules)
            || context.Match.Members.SingleOrDefault(member => member.PlayerId == context.Match.CurrentPlayerId)?.Seat != seat
            || WithMon(stored, rules) is not { } state
            || state.Pawns.FirstOrDefault(pawn => pawn.Seat == seat) is not { } pawn
            || !IsPlayable(pawn, rules)
            || rules.Tiles[pawn.Tile].Type != LifeTileType.Branch)
        {
            return null;
        }

        if (read.Choosing != seat)
        {
            return JsonSerializer.SerializeToElement(KeepOrSwitch(state, pawn, pawn.Lane ?? rules.DefaultLane), Json);
        }

        var chosen = Choose(state, pawn, rules.DefaultLane, byDefault: true, rules);
        return JsonSerializer.SerializeToElement(
            pawn.Armed?.Ability == LifeAbilities.EventPreview ? Previewed(chosen, seat, rules, context.Random) : chosen, Json);
    }

    /// <summary>
    /// The state with the highest of Double Dice's dice kept for the current player's next roll, when the turn ran out with
    /// none kept and no lane to choose; null otherwise.
    /// </summary>
    private LifeMatchState? DiceKept(LifeMatchState stored, GameRoom match)
    {
        var seat = match.Members.SingleOrDefault(member => member.PlayerId == match.CurrentPlayerId)?.Seat;
        if (stored.Choosing is not null
            || stored.Confirming is not null
            || !_rulesByVersion.TryGetValue(stored.RulesVersion, out var rules)
            || WithMon(stored, rules) is not { } state
            || state.Pawns.FirstOrDefault(pawn => pawn.Seat == seat) is not { Dice: { } dice, Kept: null } pawn
            || !IsPlayable(pawn, rules))
        {
            return null;
        }

        return state with { Pawns = Replace(state, pawn with { Kept = dice.Max() }) };
    }

    /// <summary>
    /// The board and the pawns with each player's portfolio (liquid MON, assets, bonuses, liabilities and the total, as
    /// the server values them, DEC-042) and the lane chosen at the fork they stand on, who is choosing a lane and the
    /// lane taken if they don't, and what a fork's stake returns on each lane, the same for every player: Chog Life has
    /// no hidden information on the board, except an Event Preview, which only the player who used it sees (STORY-042,
    /// DEC-044). Pawns of players who left are taken off. A state this server can't read, or of a version it no longer
    /// knows, shows nothing.
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
        var finished = state.Finished ?? [];
        var viewer = context.Match.Members.SingleOrDefault(member => member.PlayerId == context.PlayerId)?.Seat;
        var view = new LifeBoardView(
            rules.Version,
            rules.Tiles.Select(ToView).ToArray(),
            WithMon(state, rules).Pawns
                .Where(pawn => playing.Contains(pawn.Seat))
                .OrderBy(pawn => pawn.Seat)
                .Select(pawn => ToView(pawn, PlaceOf(finished, pawn.Seat)))
                .ToArray(),
            state.LastMove,
            state.Choosing is { } seat && playing.Contains(seat) ? seat : null,
            state.Confirming is { } deciding && playing.Contains(deciding) ? deciding : null,
            rules.DefaultLane,
            rules.ForkStake > 0 ? rules.ForkStake : null,
            rules.ForkStake > 0 ? [rules.Stakes[LifeLane.Safe], rules.Stakes[LifeLane.Degen]] : null,
            rules.FinishBonus > 0 ? rules.FinishBonus : null,
            state.Ended,
            viewer is { } own && playing.Contains(own) ? PreviewFor(WithMon(state, rules), own, rules) : null,
            state.Tax);
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

    private static LifePawnView ToView(LifePawn pawn, int? place)
    {
        var portfolio = PortfolioOf(pawn);
        return new LifePawnView(
            pawn.Seat,
            pawn.Tile,
            portfolio.Liquid,
            pawn.Lane,
            pawn.Stake,
            portfolio.Assets,
            portfolio.Bonuses,
            portfolio.Liabilities,
            portfolio.Total,
            place,
            pawn.Armed?.Ability,
            pawn.Dice,
            pawn.Kept);
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

    /// <summary>
    /// The stored state: the rules version the match started with, where each seat's pawn stands with its liquid MON,
    /// the latest move and the seat whose roll stopped on a fork and who is still to choose a lane, the seats that reached
    /// the Finish in the order they arrived (null for none), once the match is over, what ended it, and the seat that
    /// previewed its next move on the lane it chose and is still to keep or switch it (STORY-042, DEC-044), and a Tax the
    /// Rich used this turn, until its player rolls (STORY-091, DEC-078).
    /// </summary>
    private sealed record LifeMatchState(
        int RulesVersion,
        IReadOnlyList<LifePawn> Pawns,
        LifeMove? LastMove = null,
        int? Choosing = null,
        IReadOnlyList<int>? Finished = null,
        LifeEnding? Ended = null,
        int? Confirming = null,
        LifeTax? Tax = null);

    /// <summary>What ended the match: every player still playing finished, or the time cap was up.</summary>
    private enum LifeEnding
    {
        Finish,
        TimeUp,
    }

    /// <summary>
    /// A seat's move: the roll (null when a pawn on the Finish passed), the tiles it left and reached, every tile it
    /// passed on the way, ending with <paramref name="To"/>, and the signed MON the tile it stopped on moved (null for a
    /// tile that does nothing; a loss the liquid MON couldn't cover counts in full, the rest having become a liability).
    /// A move that stopped on a fork gets the <paramref name="Lane"/> chosen there, <paramref name="ByDefault"/> when the
    /// turn ran out and the rules chose it, and the MON <paramref name="Staked"/> on it. A move that rejoined the main
    /// track with a stake says what it paid out (<paramref name="Matured"/>). A move that reached the Finish gives the
    /// player's <paramref name="Place"/> in the order of arrival and the finish <paramref name="Bonus"/> it won, if any.
    /// An armed ability that acted on the tile's outcome says what it did (STORY-042, DEC-044): the bonuses a Reward Boost
    /// added (<paramref name="Boost"/>), the MON Loss Protection kept from a loss (<paramref name="Saved"/>) or the signed
    /// MON of the worst event outcome a Lucky Reroll discarded (<paramref name="Rerolled"/>) of its
    /// <paramref name="Draws"/> (STORY-090). A lane switched after an Event
    /// Preview sets <paramref name="Switched"/>. A roll after Double Dice gives every die rolled (<paramref name="Dice"/>),
    /// its <paramref name="Roll"/> the one kept, and a Tax the Rich used before it what it took (<paramref name="Taxed"/>)
    /// (STORY-091, DEC-078).
    /// </summary>
    private sealed record LifeMove(
        int Seat,
        int? Roll,
        int From,
        int To,
        IReadOnlyList<int> Path,
        int? Change = null,
        LifeLane? Lane = null,
        bool? ByDefault = null,
        int? Staked = null,
        LifeMaturity? Matured = null,
        int? Place = null,
        int? Bonus = null,
        int? Boost = null,
        int? Saved = null,
        int? Rerolled = null,
        bool? Switched = null,
        int? Draws = null,
        IReadOnlyList<int>? Dice = null,
        LifeTax? Taxed = null);

    /// <summary>A stake of <paramref name="Stake"/> MON on <paramref name="Lane"/> that paid <paramref name="Payout"/> MON back.</summary>
    private sealed record LifeMaturity(LifeLane Lane, int Stake, int Payout);

    /// <summary>
    /// A seat's pawn: the tile it stands on, the player's liquid MON (null in states from before STORY-038), on a fork
    /// the lane it will leave by (null until chosen), the MON staked on the lane it took until that pays out, and the
    /// player's bonuses and liabilities (null for none), the ability its player armed until it acts and, on a fork after
    /// an Event Preview, the draws its next roll will use (STORY-042, DEC-044), which only its player may see, and its
    /// player's statistics so far (STORY-043, DEC-046), which only the results show. After Double Dice it holds the dice
    /// rolled until its next roll and, once a turn ran out, the die kept of them (STORY-091, DEC-078).
    /// </summary>
    private sealed record LifePawn(
        int Seat,
        int Tile,
        int? Mon,
        LifeLane? Lane = null,
        LifeAsset? Stake = null,
        int? Bonuses = null,
        int? Liabilities = null,
        LifeArmed? Armed = null,
        IReadOnlyList<int>? Preview = null,
        LifeStats? Stats = null,
        IReadOnlyList<int>? Dice = null,
        int? Kept = null);

    /// <summary>
    /// A pawn as every player sees it, with its player's portfolio: <c>mon</c> is the liquid MON. <c>place</c> is the
    /// order in which its player reached the Finish, from 1; null while still on the board. <c>armed</c> is the id of the
    /// ability its player armed and that hasn't acted yet (STORY-042, DEC-044). <c>dice</c> are Double Dice's, rolled for
    /// its player's next roll, and <c>kept</c> the one kept when their turn ran out (STORY-091, DEC-078).
    /// </summary>
    private sealed record LifePawnView(
        int Seat,
        int Tile,
        int Mon,
        LifeLane? Lane,
        LifeAsset? Stake,
        int Assets,
        int Bonuses,
        int Liabilities,
        int Portfolio,
        int? Place,
        string? Armed,
        IReadOnlyList<int>? Dice,
        int? Kept);

    private sealed record LifeBoardView(
        int RulesVersion,
        IReadOnlyList<LifeTileView> Tiles,
        IReadOnlyList<LifePawnView> Pawns,
        LifeMove? LastMove,
        int? Choosing,
        int? Confirming,
        LifeLane DefaultLane,
        int? ForkStake,
        IReadOnlyList<LifeLaneStake>? StakeReturns,
        int? FinishBonus,
        LifeEnding? Ended,
        LifePreview? Preview,
        LifeTax? Tax);

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
