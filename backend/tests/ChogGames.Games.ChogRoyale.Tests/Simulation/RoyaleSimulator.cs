using System.Text.Json;
using ChogGames.Core.Matches;
using ChogGames.Core.Rooms;
using ChogGames.Games.ChogRoyale.Abilities;
using ChogGames.Games.ChogRoyale.Rules;

namespace ChogGames.Games.ChogRoyale.Tests.Simulation;

/// <summary>How a simulated player chooses their card and target.</summary>
public enum RoyaleStyle
{
    /// <summary>Any card in hand, or now and then HODL, at a random opponent.</summary>
    Random,

    /// <summary>
    /// Rug Pull or Whale Swap at the richest opponent, any of them on a tie, when they hold one, otherwise as
    /// <see cref="Random"/>.
    /// </summary>
    Aggressive,

    /// <summary>Pump, Airdrop or Diamond Hands when they hold one, otherwise as <see cref="Random"/>.</summary>
    Saver,
}

/// <summary>
/// A simulated player: how they play, the ability they lock (null for none), the chance they let any one round pass
/// without acting (AFK), how long they think before acting, and the round they leave before deciding, if they do.
/// </summary>
public sealed record RoyaleSimPlayer(
    RoyaleStyle Style,
    string? Ability = null,
    double AfkChance = 0,
    double MinThink = 2,
    double MaxThink = 10,
    int? LeavesInRound = null);

/// <summary>One player's outcome, by seat: their rank and result, or none for a player who left.</summary>
public sealed record RoyaleSimResult(RoyaleSimPlayer Player, int Seat, int? Rank, int Mon, bool Left);

/// <summary>
/// One simulated match: its final status, how long it lasted on the server's clock, the rounds resolved and how many of
/// them before their deadline because everyone had locked, the commands and ability uses accepted, the stored state, its
/// ledger and each player's outcome.
/// </summary>
public sealed record RoyaleSimMatch(
    int Seed,
    RoomStatus Ending,
    TimeSpan Duration,
    int Rounds,
    int EarlyRounds,
    int Commands,
    int AbilityUses,
    string State,
    IReadOnlyList<RoyaleRoundLedger> Ledger,
    IReadOnlyList<RoyaleSimResult> Players)
{
    public IEnumerable<RoyaleSimResult> Winners => Players.Where(p => p.Rank == 1);
}

/// <summary>
/// STORY-079 (ROYALE-063, ROYALE-065): plays whole Chog Royale matches through the real command path, as the API and the
/// round sweep do: every submission and ability is checked by <see cref="GameRoom.CheckCommand"/>, decided by
/// <see cref="ChogRoyaleMatchRules"/> or the <see cref="Core.Abilities.AbilityEngine"/> and applied to the room; a round
/// resolves when everyone still playing has locked or at its deadline, through the rules and
/// <see cref="GameRoom.ResolveRound"/>, on a virtual clock. Every draw comes from a source seeded per match, so a match
/// replays exactly from its seed. The simulated players only send what a player could, so any refusal is a defect and
/// throws; so does a round that doesn't resolve, a balance outside its range or a ledger that doesn't match the balances.
/// </summary>
/// <param name="rules">The rules version to play; new matches' (version 2, DEC-090) by default.</param>
public sealed class RoyaleSimulator(RoyaleRules? rules = null)
{
    private readonly RoyaleRules _rules = rules ?? RoyaleRulesV2.Rules;

    /// <summary>A source of chance seeded for one match; the server's is <c>SecureRandomSource</c>.</summary>
    private sealed class SeededRandom(int seed) : IRandomSource
    {
        private readonly Random _random = new(seed);

        public int Next(int minInclusive, int maxExclusive) => _random.Next(minInclusive, maxExclusive);
    }

    public RoyaleSimMatch Play(int seed, IReadOnlyList<RoyaleSimPlayer> profiles)
    {
        var table = new RoyaleTable(
            profiles.Count, new SeededRandom(seed), new ChogRoyaleMatchRules([_rules]), [.. profiles.Select(p => p.Ability)]);
        var behaviour = new Random(unchecked((seed * 7919) + 17));
        var abilityRound = profiles
            .Select(p => p.Ability == RoyaleAbilities.AlphaLeak ? behaviour.Next(1, _rules.Rounds) : behaviour.Next(1, _rules.Rounds + 1))
            .ToArray();
        var used = new bool[profiles.Count];
        var started = table.Match.StartedAt!.Value;
        var commands = 0;
        var abilityUses = 0;
        var early = 0;

        for (var round = 1; table.Match.Status == RoomStatus.Active; round++)
        {
            if (round > _rules.Rounds)
            {
                throw new InvalidOperationException($"Seed {seed}: the match is still active after round {_rules.Rounds}.");
            }

            Assert.Equal(round, table.Match.TurnNumber);
            var opens = table.Now;
            foreach (var (profile, seat) in profiles.Select((p, i) => (p, i + 1)).Where(p => p.p.LeavesInRound == round))
            {
                if (table.Match.Status == RoomStatus.Active && Playing(table, seat))
                {
                    table.Match.Leave(table.Seat(seat), opens);
                }
            }

            if (table.Match.Status != RoomStatus.Active)
            {
                break;
            }

            var deadline = table.Match.TurnEndsAt!.Value;
            var turns = profiles
                .Select((profile, i) => (Profile: profile, Seat: i + 1, Think: profile.MinThink + (behaviour.NextDouble() * (profile.MaxThink - profile.MinThink))))
                .Where(p => Playing(table, p.Seat) && behaviour.NextDouble() >= p.Profile.AfkChance)
                .OrderBy(p => p.Think)
                .ToList();
            foreach (var (profile, seat, think) in turns)
            {
                var at = opens + TimeSpan.FromSeconds(think);
                if (at >= deadline || table.Match.Status != RoomStatus.Active || table.Match.TurnNumber != round)
                {
                    break;
                }

                table.Now = at;
                var hand = table.Hand(seat);
                var (card, target) = Choose(table, profile.Style, seat, hand, behaviour);
                if (profile.Ability is { } ability && !used[seat - 1] && UsesNow(ability, round, abilityRound[seat - 1], card))
                {
                    used[seat - 1] = true;
                    Assert.True(table.UseAbility(seat, ability) is null, $"Seed {seed}: round {round}, seat {seat}'s {ability} was refused.");
                    abilityUses++;
                }

                var locks = behaviour.NextDouble() < 0.8;
                var decision = card is null ? table.Hodl(seat, locks) : table.Submit(seat, new { card = card.Id, target, @lock = locks });
                Assert.True(decision.Outcome == MatchRuleOutcome.Accepted, $"Seed {seed}: round {round}, seat {seat}'s action was refused: {decision.Reason}.");
                commands++;
                if (table.Match.IsRoundDue(table.Now))
                {
                    break;
                }
            }

            var before = table.Stored.Ledger.Length;
            early += table.Match.IsRoundDue(table.Now) && table.Now < table.Match.TurnEndsAt ? 1 : 0;
            table.Resolve();
            Check(table, seed, round, before);
        }

        var members = table.Match.Members.OrderBy(m => m.Seat).ToArray();
        var stored = table.Stored;
        return new RoyaleSimMatch(
            seed,
            table.Match.Status,
            table.Match.EndedAt!.Value - started,
            stored.Ledger.Length,
            early,
            commands,
            abilityUses,
            table.Match.State!,
            stored.Ledger,
            [.. members.Select((m, i) => new RoyaleSimResult(profiles[i], m.Seat!.Value, m.Rank, stored.Players.Single(p => p.Seat == m.Seat).Mon, m.HasLeft))]);
    }

    private static bool Playing(RoyaleTable table, int seat) => table.Match.Members.Single(m => m.Seat == seat) is { HasLeft: false };

    /// <summary>
    /// The card the player plays, or null for HODL, and its target: one in ten actions is HODL; otherwise their style's
    /// card if they hold one, else any card in hand, aimed at a random opponent still playing, or the richest one.
    /// </summary>
    private static (RoyaleHandCard? Card, int? Target) Choose(RoyaleTable table, RoyaleStyle style, int seat, RoyaleHandCard[] hand, Random behaviour)
    {
        if (behaviour.NextDouble() < 0.1 || hand.Length == 0)
        {
            return (null, null);
        }

        var opponents = table.Stored.Players.Where(p => p.Seat != seat && Playing(table, p.Seat)).ToArray();
        var preferred = style switch
        {
            RoyaleStyle.Aggressive => hand.Where(c => c.Card.IsTargeted()).ToArray(),
            RoyaleStyle.Saver => hand.Where(c => c.Card is RoyaleCard.Pump or RoyaleCard.Airdrop or RoyaleCard.DiamondHands).ToArray(),
            _ => [],
        };
        var pool = preferred.Length > 0 ? preferred : hand;
        var card = pool[behaviour.Next(pool.Length)];
        if (!card.Card.IsTargeted())
        {
            return (card, null);
        }

        var richest = opponents.Where(p => p.Mon == opponents.Max(o => o.Mon)).ToArray();
        var target = style == RoyaleStyle.Aggressive
            ? richest[behaviour.Next(richest.Length)].Seat
            : opponents[behaviour.Next(opponents.Length)].Seat;
        return (card, target);
    }

    /// <summary>
    /// Whether the player uses their ability before this action: Double Pump with the first Pump they play, the others in
    /// their planned round.
    /// </summary>
    private static bool UsesNow(string ability, int round, int planned, RoyaleHandCard? card) =>
        ability == RoyaleAbilities.DoublePump ? card?.Card == RoyaleCard.Pump : round == planned;

    /// <summary>After each resolution: one more ledger round, every balance in range and equal to its ledger entry.</summary>
    private void Check(RoyaleTable table, int seed, int round, int before)
    {
        var stored = table.Stored;
        if (table.Match.Status == RoomStatus.Abandoned)
        {
            return;
        }

        Assert.True(stored.Ledger.Length == before + 1, $"Seed {seed}: round {round} didn't resolve exactly once.");
        foreach (var entry in stored.Ledger[^1].Entries)
        {
            var mon = stored.Players.Single(p => p.Seat == entry.Seat).Mon;
            Assert.True(mon >= 0 && mon <= _rules.MaxMon && entry.Balance == mon, $"Seed {seed}: round {round}, seat {entry.Seat} holds {mon}, ledger {entry.Balance}.");
        }

        Assert.All(stored.Players, p => Assert.Null(p.Action));
    }

    /// <summary>The ledger as JSON, for comparing two plays of one seed.</summary>
    public static string History(RoyaleSimMatch match) => JsonSerializer.Serialize(match.Ledger, RoyaleTable.Json);
}
