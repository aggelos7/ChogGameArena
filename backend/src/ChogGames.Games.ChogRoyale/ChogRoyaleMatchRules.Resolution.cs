using System.Text.Json;
using ChogGames.Core.Matches;
using ChogGames.Core.Rooms;
using ChogGames.Games.ChogRoyale.Rules;

namespace ChogGames.Games.ChogRoyale;

/// <summary>
/// Round resolution (STORY-074, ROYALE-026–032, DEC-085): the engine locks the round and calls it once; the same state,
/// actions and randomness always give the same balances and ledger.
/// </summary>
public sealed partial class ChogRoyaleMatchRules
{
    /// <summary>
    /// The phases of a round, in order (ROYALE-027). Within a phase, plays resolve in the round's seat priority.
    /// </summary>
    public enum Phase
    {
        /// <summary>Balances and choices are frozen; effects read this snapshot, not each other's results.</summary>
        Snapshot,
        Chaos,
        Defenses,
        Gains,
        Rugs,
        Swaps,
        Market,
        Clamp,
    }

    /// <summary>The phase a card acts in (ROYALE-027).</summary>
    public static Phase PhaseOf(RoyaleCard card) => card switch
    {
        RoyaleCard.Chaos => Phase.Chaos,
        RoyaleCard.DiamondHands => Phase.Defenses,
        RoyaleCard.Pump or RoyaleCard.Airdrop => Phase.Gains,
        RoyaleCard.RugPull => Phase.Rugs,
        RoyaleCard.WhaleSwap => Phase.Swaps,
        _ => throw new ArgumentOutOfRangeException(nameof(card), card, "Not a Chog Royale card."),
    };

    /// <summary>
    /// The round's seat priority (ROYALE-028): the seats of the players still playing in order, rotated by one each round,
    /// so round 1 starts with the lowest seat, round 2 with the next, and so on.
    /// </summary>
    public static int[] Priority(IReadOnlyList<int> seats, int round)
    {
        var ordered = seats.Order().ToArray();
        if (ordered.Length == 0)
        {
            return [];
        }

        var start = (round - 1) % ordered.Length;
        return [.. ordered[start..], .. ordered[..start]];
    }

    /// <summary>
    /// Resolves the round (DEC-085): each player still playing plays their action, or HODL if they submitted none
    /// (ROYALE-008); the phases run in order against the pre-round snapshot under the round's market event and any Chaos
    /// modifier; every balance is clamped to the rules' range; the ledger gains the round; then each played card is
    /// replaced from the deck in seat order and the next round's event is drawn, except after the last round, which ends
    /// the match ranked by its tie-breaks (STORY-078). Draws happen in that order: card effects (Chaos, then Airdrops, in
    /// resolution order), replacements, the next event. A state this server can't play is left as it is, and the last
    /// round still ends the match, everyone sharing first place.
    /// </summary>
    public MatchRoundResolution ResolveRound(MatchRoundContext context)
    {
        var match = context.Match;
        if (Playable(context.State, match) is not var (state, rules))
        {
            return Unplayable(context);
        }

        var seats = Playing(match).Select(m => m.Seat!.Value).ToList();
        var priority = Priority(seats, state.Round);
        var plays = priority.Select(seat => PlayOf(state.Players.Single(p => p.Seat == seat), seats)).ToList();
        var work = new RoundWork(
            rules, state.Event, state.Players.Where(p => seats.Contains(p.Seat)).ToDictionary(p => p.Seat, p => p.Mon), context.Random);

        foreach (var phase in new[] { Phase.Chaos, Phase.Defenses, Phase.Gains, Phase.Rugs, Phase.Swaps })
        {
            foreach (var play in plays.Where(p => p.Card is { } card && PhaseOf(card) == phase))
            {
                Act(play, work);
            }
        }

        ApplyMarket(work);
        var balances = work.Balances.ToDictionary(b => b.Key, b => rules.Clamp(b.Value));

        var entries = plays
            .OrderBy(play => play.Seat)
            .Select(play => new RoyaleLedgerEntry(
                play.Seat,
                play.Card,
                play.Target,
                play.ByDefault,
                work.Outcome.TryGetValue(play.Seat, out var outcome) ? outcome.Outcome : null,
                outcome.Amount,
                work.Defense.TryGetValue(play.Seat, out var defense) ? defense : null,
                work.Lost.GetValueOrDefault(play.Seat),
                work.Market.GetValueOrDefault(play.Seat),
                balances[play.Seat] - work.Snapshot[play.Seat],
                balances[play.Seat]))
            .ToArray();

        var last = state.Round >= rules.Rounds;
        var nextCard = state.NextCard;
        var players = state.Players
            .OrderBy(p => p.Seat)
            .Select(player =>
            {
                if (!seats.Contains(player.Seat))
                {
                    return player with { Action = null };
                }

                var played = plays.Single(p => p.Seat == player.Seat).CardId;
                var hand = player.Hand.Where(c => c.Id != played).ToList();
                if (played is not null && !last)
                {
                    hand.AddRange(Deal(rules, rules.HandSize - hand.Count, ref nextCard, context.Random));
                }

                return player with { Mon = balances[player.Seat], Hand = [.. hand], Action = null };
            })
            .ToArray();

        var ledger = (RoyaleRoundLedger[])[.. state.Ledger, new RoyaleRoundLedger(state.Round, state.Event, work.Chaos, priority, entries)];
        var resolved = state with
        {
            Round = last ? state.Round : state.Round + 1,
            Event = last ? state.Event : DrawEvent(rules, context.Random),
            NextCard = nextCard,
            Players = players,
            Ledger = ledger,
        };
        return last
            ? new MatchRoundResolution(Serialize(resolved), Standings(match, rules, players, ledger))
            : new MatchRoundResolution(Serialize(resolved));
    }

    private MatchRoundResolution Unplayable(MatchRoundContext context)
    {
        var unchanged = context.State is { ValueKind: JsonValueKind.Object } state ? state : Serialize(new { });
        var rounds = Readable(context.State) is { } readable && _rulesByVersion.TryGetValue(readable.RulesVersion, out var rules)
            ? rules.Rounds
            : _current.Rounds;
        return context.Match.TurnNumber < rounds
            ? new MatchRoundResolution(unchanged)
            : new MatchRoundResolution(unchanged, [.. Playing(context.Match).Select(m => new MatchStanding(m.PlayerId, 1))]);
    }

    /// <summary>A round's market event, one weighted draw from the server's randomness (ROYALE-023, DEC-088).</summary>
    private static RoyaleMarketEvent DrawEvent(RoyaleRules rules, IRandomSource random) =>
        rules.Market.EventAt(random.Next(0, rules.Market.EventWeight));

    /// <summary>One player's play this round, as the resolution reads it from the snapshot.</summary>
    /// <param name="CardId">The hand card played, which leaves the hand; null for HODL.</param>
    /// <param name="Target">The seat aimed at; void (no effect) once that player has left.</param>
    private sealed record Play(int Seat, string? CardId, RoyaleCard? Card, int? Target, bool TargetPlaying, bool ByDefault);

    /// <summary>The player's submitted action, or HODL by default; a card no longer in the hand is HODL too.</summary>
    private static Play PlayOf(RoyalePlayer player, IReadOnlyCollection<int> playing)
    {
        if (player.Action is not { Hodl: false, Card: { } id } action || player.Hand.FirstOrDefault(c => c.Id == id) is not { } card)
        {
            return new Play(player.Seat, null, null, null, false, ByDefault: player.Action is null);
        }

        return new Play(player.Seat, id, card.Card, action.Target, action.Target is { } target && playing.Contains(target), false);
    }

    /// <summary>
    /// The round being worked out: the snapshot every effect reads (ROYALE-027 phase 1), the running balances, the round's
    /// event and Chaos modifier, the shields still up, who has swapped, and what each card, defense and the market did,
    /// for the ledger.
    /// </summary>
    private sealed class RoundWork(RoyaleRules rules, RoyaleMarketEvent roundEvent, Dictionary<int, int> snapshot, IRandomSource random)
    {
        public RoyaleRules Rules { get; } = rules;

        public RoyaleMarketEvent Event { get; } = roundEvent;

        /// <summary>The modifier the round's first Chaos drew, if any (ROYALE-024).</summary>
        public RoyaleMarketEvent? Chaos { get; set; }

        public IReadOnlyDictionary<int, int> Snapshot { get; } = snapshot;

        public Dictionary<int, long> Balances { get; } = snapshot.ToDictionary(b => b.Key, b => (long)b.Value);

        /// <summary>Seats whose Diamond Hands hasn't blocked anything yet.</summary>
        public HashSet<int> Shields { get; } = [];

        /// <summary>Seats already in a successful Whale Swap this round (ROYALE-030).</summary>
        public HashSet<int> Swapped { get; } = [];

        public Dictionary<int, (RoyaleOutcome Outcome, int Amount)> Outcome { get; } = [];

        public Dictionary<int, RoyaleDefense> Defense { get; } = [];

        public Dictionary<int, int> Lost { get; } = [];

        public Dictionary<int, int> Market { get; } = [];

        public IRandomSource Random { get; } = random;

        /// <summary>Whether the round's event or its Chaos modifier is <paramref name="modifier"/>.</summary>
        public bool Has(RoyaleMarketEvent modifier) => Event == modifier || Chaos == modifier;

        /// <summary>
        /// Whether the target's shield takes this hostile card, spending it: a shield blocks the first one aimed at its
        /// player in resolution order (ROYALE-029).
        /// </summary>
        public bool Blocks(int target)
        {
            if (!Shields.Remove(target))
            {
                return false;
            }

            Defense[target] = RoyaleDefense.Blocked;
            return true;
        }

        /// <summary>Moves <paramref name="amount"/> MON from <paramref name="from"/> to <paramref name="to"/>, as a loss for the first.</summary>
        public void Move(int from, int to, int amount)
        {
            Balances[from] -= amount;
            Balances[to] += amount;
            if (amount > 0)
            {
                Lost[from] = Lost.GetValueOrDefault(from) + amount;
            }
        }
    }

    /// <summary>
    /// A card's effect in its phase (STORY-075, STORY-076, DEC-086, DEC-087), recorded with its outcome and amount:
    /// <list type="bullet">
    /// <item>Chaos: the round's first draws one modifier from the Chaos pool, any but the round's event; later ones are <c>Taken</c>.</item>
    /// <item>Diamond Hands: gains its MON and raises a shield that blocks the first Rug Pull, else Whale Swap, aimed at its player.</item>
    /// <item>Pump: gains its MON, times Meme Hype's or Market Crash's percentage, rounded down.</item>
    /// <item>Airdrop: gains a server-random amount in its range.</item>
    /// <item>Rug Pull: steals its MON, plus Whale Incoming's bonus, never more than the target holds by then.</item>
    /// <item>
    /// Whale Swap: each side gives its share of its pre-round balance, within the cap and what it holds by then; a player
    /// is in at most one successful swap, the earliest in priority, and a swap that can't happen doesn't spend a shield.
    /// </item>
    /// </list>
    /// A card aimed at a player who left has no effect.
    /// </summary>
    private static void Act(Play play, RoundWork work)
    {
        var effects = work.Rules.Effects;
        var seat = play.Seat;
        work.Outcome[seat] = play.Card switch
        {
            RoyaleCard.Chaos when work.Chaos is not null => (RoyaleOutcome.Taken, 0),
            RoyaleCard.Chaos => DrawChaos(work),
            RoyaleCard.DiamondHands => RaiseShield(seat, work),
            RoyaleCard.Pump => Gain(seat, PumpGain(work), work),
            RoyaleCard.Airdrop => Gain(seat, work.Random.Next(effects.AirdropMin, effects.AirdropMax + 1), work),
            RoyaleCard.RugPull or RoyaleCard.WhaleSwap when !play.TargetPlaying => (RoyaleOutcome.TargetLeft, 0),
            RoyaleCard.RugPull => Rug(seat, play.Target!.Value, work),
            RoyaleCard.WhaleSwap => Swap(seat, play.Target!.Value, work),
            _ => throw new InvalidOperationException("Only a played card acts."),
        };
    }

    private static (RoyaleOutcome, int) DrawChaos(RoundWork work)
    {
        var market = work.Rules.Market;
        work.Chaos = market.ChaosAt(work.Event, work.Random.Next(0, market.ChaosWeight(work.Event)));
        return (RoyaleOutcome.Applied, 0);
    }

    private static (RoyaleOutcome, int) RaiseShield(int seat, RoundWork work)
    {
        work.Shields.Add(seat);
        work.Defense[seat] = RoyaleDefense.Raised;
        return Gain(seat, work.Rules.Effects.DiamondHands, work);
    }

    private static (RoyaleOutcome, int) Gain(int seat, int amount, RoundWork work)
    {
        work.Balances[seat] += amount;
        return (RoyaleOutcome.Applied, amount);
    }

    /// <summary>Pump's gain under the round's modifiers, rounded down once at the end (ROYALE-025).</summary>
    private static int PumpGain(RoundWork work)
    {
        var market = work.Rules.Market;
        long gain = work.Rules.Effects.Pump;
        long divisor = 1;
        foreach (var (modifier, percent) in new[] { (RoyaleMarketEvent.MemeHype, market.HypePercent), (RoyaleMarketEvent.MarketCrash, market.CrashPercent) })
        {
            if (work.Has(modifier))
            {
                gain *= percent;
                divisor *= 100;
            }
        }

        return (int)(gain / divisor);
    }

    private static (RoyaleOutcome, int) Rug(int seat, int target, RoundWork work)
    {
        if (work.Blocks(target))
        {
            return (RoyaleOutcome.Blocked, 0);
        }

        var steal = work.Rules.Effects.RugPull + (work.Has(RoyaleMarketEvent.WhaleIncoming) ? work.Rules.Market.WhaleBonus : 0);
        var taken = (int)Math.Clamp(work.Balances[target], 0, steal);
        if (taken == 0)
        {
            return (RoyaleOutcome.Empty, 0);
        }

        work.Move(target, seat, taken);
        return (RoyaleOutcome.Applied, taken);
    }

    private static (RoyaleOutcome, int) Swap(int seat, int target, RoundWork work)
    {
        if (work.Swapped.Contains(seat) || work.Swapped.Contains(target))
        {
            return (RoyaleOutcome.Taken, 0);
        }

        if (work.Blocks(target))
        {
            return (RoyaleOutcome.Blocked, 0);
        }

        var effects = work.Rules.Effects;
        var gives = (int)Math.Clamp(work.Balances[seat], 0, effects.SwapAmount(work.Snapshot[seat]));
        var gets = (int)Math.Clamp(work.Balances[target], 0, effects.SwapAmount(work.Snapshot[target]));
        work.Balances[seat] += gets - gives;
        work.Balances[target] += gives - gets;
        if (gets > gives)
        {
            work.Lost[target] = work.Lost.GetValueOrDefault(target) + (gets - gives);
        }

        work.Swapped.Add(seat);
        work.Swapped.Add(target);
        return (RoyaleOutcome.Applied, gets - gives);
    }

    /// <summary>
    /// The round's market effects for everyone after the cards (phase 7, STORY-077, DEC-088): Community Airdrop, from the
    /// event or Chaos, gives every player still playing its MON.
    /// </summary>
    private static void ApplyMarket(RoundWork work)
    {
        if (!work.Has(RoyaleMarketEvent.CommunityAirdrop))
        {
            return;
        }

        foreach (var seat in work.Snapshot.Keys)
        {
            work.Balances[seat] += work.Rules.Market.CommunityAirdrop;
            work.Market[seat] = work.Rules.Market.CommunityAirdrop;
        }
    }
}
