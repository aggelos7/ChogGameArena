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
    /// (ROYALE-008); the phases run in order against the pre-round snapshot; every balance is clamped to the rules' range;
    /// the ledger gains the round; then each played card is replaced from the deck in seat order, except after the last
    /// round, which ends the match ranked by MON. Draws happen in that order: card effects, then replacements. A state this
    /// server can't play is left as it is, and the last round still ends the match, everyone sharing first place.
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
        var work = new RoundWork(rules, state.Players.Where(p => seats.Contains(p.Seat)).ToDictionary(p => p.Seat, p => p.Mon), context.Random);

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
                work.Defense.GetValueOrDefault(play.Seat),
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

        var resolved = state with
        {
            Round = last ? state.Round : state.Round + 1,
            NextCard = nextCard,
            Players = players,
            Ledger = [.. state.Ledger, new RoyaleRoundLedger(state.Round, priority, entries)],
        };
        return last
            ? new MatchRoundResolution(Serialize(resolved), Standings(match, players))
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
    /// The round being worked out: the snapshot every effect reads (ROYALE-027 phase 1), the running balances, and what
    /// each player's defense and the market did, for the ledger.
    /// </summary>
    private sealed class RoundWork(RoyaleRules rules, Dictionary<int, int> snapshot, IRandomSource random)
    {
        public RoyaleRules Rules { get; } = rules;

        public IReadOnlyDictionary<int, int> Snapshot { get; } = snapshot;

        public Dictionary<int, long> Balances { get; } = snapshot.ToDictionary(b => b.Key, b => (long)b.Value);

        public Dictionary<int, string> Defense { get; } = [];

        public Dictionary<int, int> Market { get; } = [];

        public IRandomSource Random { get; } = random;
    }

    /// <summary>
    /// A card's effect in its phase. No card has one yet: Pump, Rug Pull and Diamond Hands come with STORY-075, Whale
    /// Swap, Airdrop and Chaos with STORY-076, so every card is played and recorded, and moves no MON.
    /// </summary>
    private static void Act(Play play, RoundWork work) => _ = (play, work);

    /// <summary>The round's market effects for everyone (phase 7). None yet: market events come with STORY-077.</summary>
    private static void ApplyMarket(RoundWork work) => _ = work;
}
