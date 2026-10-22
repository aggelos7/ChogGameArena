using System.Text.Json;
using ChogGames.Core.Matches;
using ChogGames.Core.Rooms;
using ChogGames.Games.ChogRoyale.Abilities;
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
    /// modifier, with the abilities used this round (STORY-080, DEC-091); every balance is clamped to the rules' range; the
    /// ledger gains the round; then each played card is replaced from the deck in seat order and the next round's event is
    /// drawn, unless Alpha Leak drew it already, except after the last round, which ends the match ranked by its
    /// tie-breaks (STORY-078). Draws happen in that order: card effects (Chaos, then Airdrops, in resolution order),
    /// replacements, the next event. A state this server can't play is left as it is, and the last round still ends the
    /// match, everyone sharing first place.
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
        var playing = state.Players.Where(p => seats.Contains(p.Seat)).ToList();
        var work = new RoundWork(rules, state.Event, playing.ToDictionary(p => p.Seat, p => p.Mon), context.Random);
        foreach (var player in playing)
        {
            work.Use(player.Seat, player.Ability);
        }

        foreach (var phase in new[] { Phase.Chaos, Phase.Defenses, Phase.Gains, Phase.Rugs, Phase.Swaps })
        {
            if (phase == Phase.Defenses)
            {
                RaiseExtraShields(priority, work);
            }

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
                balances[play.Seat],
                playing.Single(p => p.Seat == play.Seat).Ability?.Id))
            .ToArray();

        var last = state.Round >= rules.Rounds;
        var nextCard = state.NextCard;
        var players = state.Players
            .OrderBy(p => p.Seat)
            .Select(player =>
            {
                if (!seats.Contains(player.Seat))
                {
                    return player with { Action = null, Ability = null };
                }

                var played = plays.Single(p => p.Seat == player.Seat).CardId;
                var hand = player.Hand.Where(c => c.Id != played).ToList();
                if (played is not null && !last)
                {
                    hand.AddRange(Deal(rules, rules.HandSize - hand.Count, ref nextCard, context.Random));
                }

                return player with { Mon = balances[player.Seat], Hand = [.. hand], Action = null, Ability = null };
            })
            .ToArray();

        var ledger = (RoyaleRoundLedger[])[.. state.Ledger, new RoyaleRoundLedger(state.Round, state.Event, work.Chaos, priority, entries)];
        var resolved = state with
        {
            Round = last ? state.Round : state.Round + 1,
            Event = last ? state.Event : state.NextEvent ?? DrawEvent(rules, context.Random),
            NextEvent = null,
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
    /// event and Chaos modifier, the abilities used, the shields still up, what Stop Loss still lets through, who has
    /// swapped, and what each card, defense and the market did, for the ledger.
    /// </summary>
    private sealed class RoundWork(RoyaleRules rules, RoyaleMarketEvent roundEvent, Dictionary<int, int> snapshot, IRandomSource random)
    {
        public RoyaleRules Rules { get; } = rules;

        public RoyaleMarketEvent Event { get; } = roundEvent;

        /// <summary>The modifier the round's first Chaos drew, if any (ROYALE-024).</summary>
        public RoyaleMarketEvent? Chaos { get; set; }

        public IReadOnlyDictionary<int, int> Snapshot { get; } = snapshot;

        public Dictionary<int, long> Balances { get; } = snapshot.ToDictionary(b => b.Key, b => (long)b.Value);

        /// <summary>The hostile cards each seat's shield still blocks, from Diamond Hands and Diamond Hands+ (DEC-091).</summary>
        public Dictionary<int, int> Shields { get; } = [];

        /// <summary>Seats whose Diamond Hands+ raises its shield in the defenses phase, with how many it blocks.</summary>
        public Dictionary<int, int> ExtraShields { get; } = [];

        /// <summary>Double Pump's percent, by seat.</summary>
        public Dictionary<int, int> PumpBoosts { get; } = [];

        /// <summary>The MON Stop Loss still lets other players' cards take, by seat.</summary>
        public Dictionary<int, int> StopLoss { get; } = [];

        /// <summary>Seats already in a successful Whale Swap this round (ROYALE-030).</summary>
        public HashSet<int> Swapped { get; } = [];

        public Dictionary<int, (RoyaleOutcome Outcome, int Amount)> Outcome { get; } = [];

        public Dictionary<int, RoyaleDefense> Defense { get; } = [];

        public Dictionary<int, int> Lost { get; } = [];

        public Dictionary<int, int> Market { get; } = [];

        public IRandomSource Random { get; } = random;

        /// <summary>Records the ability the player used this round, if any, for the phases to apply.</summary>
        public void Use(int seat, RoyaleAbilityUse? ability)
        {
            var target = ability?.Id switch
            {
                RoyaleAbilities.DiamondHandsPlus => ExtraShields,
                RoyaleAbilities.DoublePump => PumpBoosts,
                RoyaleAbilities.StopLoss => StopLoss,
                _ => null,
            };
            target?.Add(seat, ability!.Value);
        }

        /// <summary>Raises <paramref name="blocks"/> more of the seat's shield.</summary>
        public void Raise(int seat, int blocks)
        {
            Shields[seat] = Shields.GetValueOrDefault(seat) + blocks;
            Defense.TryAdd(seat, RoyaleDefense.Raised);
        }

        /// <summary>What other players' cards may take from the seat: <paramref name="amount"/>, within its Stop Loss.</summary>
        public int Allowed(int seat, int amount) =>
            StopLoss.TryGetValue(seat, out var left) ? Math.Min(amount, left) : amount;

        /// <summary>Whether the round's event or its Chaos modifier is <paramref name="modifier"/>.</summary>
        public bool Has(RoyaleMarketEvent modifier) => Event == modifier || Chaos == modifier;

        /// <summary>
        /// Whether the target's shield takes this hostile card, spending it: a shield blocks the first one aimed at its
        /// player in resolution order (ROYALE-029).
        /// </summary>
        public bool Blocks(int target)
        {
            if (Shields.GetValueOrDefault(target) == 0)
            {
                return false;
            }

            Shields[target]--;
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
                Lose(from, amount);
            }
        }

        /// <summary>Counts MON other players' cards took from the seat, against its Stop Loss.</summary>
        public void Lose(int seat, int amount)
        {
            Lost[seat] = Lost.GetValueOrDefault(seat) + amount;
            if (StopLoss.TryGetValue(seat, out var left))
            {
                StopLoss[seat] = left - amount;
            }
        }
    }

    /// <summary>
    /// A card's effect in its phase (STORY-075, STORY-076, DEC-086, DEC-087), recorded with its outcome and amount:
    /// <list type="bullet">
    /// <item>
    /// Chaos: the round's first draws one modifier from the Chaos pool, any but the round's event; later ones are
    /// <c>Taken</c>. Each gains its player the version's Chaos gain (DEC-090).
    /// </item>
    /// <item>Diamond Hands: gains its MON and raises a shield that blocks the first Rug Pull, else Whale Swap, aimed at its player.</item>
    /// <item>Pump: gains its MON, times Meme Hype's or Market Crash's percentage and Double Pump's, rounded down.</item>
    /// <item>Airdrop: gains a server-random amount in its range.</item>
    /// <item>
    /// Rug Pull: steals its MON, plus Whale Incoming's bonus, never more than the target holds by then or their Stop Loss
    /// lets through (<c>Stopped</c> when it lets nothing through).
    /// </item>
    /// <item>
    /// Whale Swap: each side gives its share of its pre-round balance, within the cap and what it holds by then; a player
    /// is in at most one successful swap, the earliest in priority, and a swap that can't happen doesn't spend a shield.
    /// The target's net loss stays within their Stop Loss. A successful swap gains its player the version's swap bonus.
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
            RoyaleCard.Chaos => Chaos(seat, work),
            RoyaleCard.DiamondHands => RaiseShield(seat, work),
            RoyaleCard.Pump => Gain(seat, PumpGain(seat, work), work),
            RoyaleCard.Airdrop => Gain(seat, work.Random.Next(effects.AirdropMin, effects.AirdropMax + 1), work),
            RoyaleCard.RugPull or RoyaleCard.WhaleSwap when !play.TargetPlaying => (RoyaleOutcome.TargetLeft, 0),
            RoyaleCard.RugPull => Rug(seat, play.Target!.Value, work),
            RoyaleCard.WhaleSwap => Swap(seat, play.Target!.Value, work),
            _ => throw new InvalidOperationException("Only a played card acts."),
        };
    }

    /// <summary>The round's first Chaos draws the modifier; every Chaos gains its player the version's Chaos gain (DEC-090).</summary>
    private static (RoyaleOutcome, int) Chaos(int seat, RoundWork work)
    {
        var market = work.Rules.Market;
        var outcome = RoyaleOutcome.Taken;
        if (work.Chaos is null)
        {
            work.Chaos = market.ChaosAt(work.Event, work.Random.Next(0, market.ChaosWeight(work.Event)));
            outcome = RoyaleOutcome.Applied;
        }

        work.Balances[seat] += work.Rules.Effects.ChaosGain;
        return (outcome, work.Rules.Effects.ChaosGain);
    }

    private static (RoyaleOutcome, int) RaiseShield(int seat, RoundWork work)
    {
        work.Raise(seat, 1);
        return Gain(seat, work.Rules.Effects.DiamondHands, work);
    }

    /// <summary>Diamond Hands+ shields, raised at the start of the defenses phase, in priority order (DEC-091).</summary>
    private static void RaiseExtraShields(IEnumerable<int> priority, RoundWork work)
    {
        foreach (var seat in priority.Where(work.ExtraShields.ContainsKey))
        {
            work.Raise(seat, work.ExtraShields[seat]);
        }
    }

    private static (RoyaleOutcome, int) Gain(int seat, int amount, RoundWork work)
    {
        work.Balances[seat] += amount;
        return (RoyaleOutcome.Applied, amount);
    }

    /// <summary>Pump's gain under the round's modifiers and the player's Double Pump, rounded down once at the end (ROYALE-025).</summary>
    private static int PumpGain(int seat, RoundWork work)
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

        if (work.PumpBoosts.TryGetValue(seat, out var boost))
        {
            gain *= 100 + boost;
            divisor *= 100;
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
        var held = (int)Math.Clamp(work.Balances[target], 0, steal);
        if (held == 0)
        {
            return (RoyaleOutcome.Empty, 0);
        }

        var taken = work.Allowed(target, held);
        if (taken == 0)
        {
            return (RoyaleOutcome.Stopped, 0);
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
        if (gets > gives)
        {
            gets = gives + work.Allowed(target, gets - gives);
        }

        work.Balances[seat] += gets - gives + effects.SwapBonus;
        work.Balances[target] += gives - gets;
        if (gets > gives)
        {
            work.Lose(target, gets - gives);
        }

        work.Swapped.Add(seat);
        work.Swapped.Add(target);
        return (RoyaleOutcome.Applied, gets - gives + effects.SwapBonus);
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
