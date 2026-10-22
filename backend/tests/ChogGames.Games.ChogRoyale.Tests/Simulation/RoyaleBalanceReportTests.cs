using System.Globalization;
using System.Text;
using ChogGames.Core.Rooms;
using ChogGames.Games.ChogRoyale.Rules;
using Xunit.Abstractions;

namespace ChogGames.Games.ChogRoyale.Tests.Simulation;

/// <summary>
/// STORY-079 (ROYALE-063, ROYALE-065): prints the tables of <c>docs/games/chog-royale-balancing.md</c> from seeded
/// simulations; run with <c>CHOG_BALANCE_REPORT=1 dotnet test tests/ChogGames.Games.ChogRoyale.Tests --filter
/// RoyaleBalanceReportTests --logger "console;verbosity=detailed"</c>. The seeds are fixed, so the same rules print the
/// same tables. A win is a first place, shared or not. The tables are for the current rules, version 2, with one that
/// compares the cards with version 1's (DEC-090).
/// </summary>
public class RoyaleBalanceReportTests(ITestOutputHelper output)
{
    private const int Matches = 2_000;

    private static readonly int[] TableSizes = [2, 4, 6];

    [BalanceReportFact]
    public void Print_the_balance_report()
    {
        var simulator = new RoyaleSimulator();
        var report = new StringBuilder();
        var engaged = TableSizes.ToDictionary(n => n, n => RoyaleScenarios.PlayMany(simulator, Matches, seed => RoyaleScenarios.Engaged(seed, n)));

        report.AppendLine("### Duration and endings\n");
        report.AppendLine("| Table | p10 | Median | p90 | Max | Rounds resolved early | Completed | Abandoned |");
        report.AppendLine("|---|---|---|---|---|---|---|---|");
        foreach (var n in TableSizes)
        {
            report.AppendLine(DurationRow($"{n} players, engaged (2–10 s)", engaged[n]));
            report.AppendLine(DurationRow($"{n} players, messy", RoyaleScenarios.PlayMany(simulator, Matches, seed => RoyaleScenarios.Messy(seed, n))));
        }

        report.AppendLine("\n### Final MON (engaged)\n");
        report.AppendLine("| Table | Min | p10 | Median | p90 | Max | Winner's median | Winner's lead over second (median) | Shared first |");
        report.AppendLine("|---|---|---|---|---|---|---|---|---|");
        foreach (var n in TableSizes)
        {
            var mon = engaged[n].SelectMany(m => m.Players.Select(p => p.Mon)).Order().ToArray();
            var winners = engaged[n].Select(m => m.Players.Max(p => p.Mon)).Order().ToArray();
            var leads = engaged[n].Select(m => m.Players.Select(p => p.Mon).OrderDescending().Take(2).Aggregate((a, b) => a - b)).Order().ToArray();
            var shared = engaged[n].Count(m => m.Players.Count(p => p.Rank == 1) > 1);
            report.AppendLine(Invariant($"| {n} players | {mon[0]} | {At(mon, 0.1)} | {At(mon, 0.5)} | {At(mon, 0.9)} | {mon[^1]} | {At(winners, 0.5)} | {At(leads, 0.5)} | {Percent(shared, engaged[n].Count)} |"));
        }

        report.AppendLine("\n### Cards (engaged, all table sizes)\n");
        report.AppendLine("| Card | Share of plays | Successful | Mean MON when successful | Win rate of players who played it | Fair share |");
        report.AppendLine("|---|---|---|---|---|---|");
        var all = engaged.Values.SelectMany(m => m).ToList();
        var plays = all.SelectMany(m => m.Ledger.SelectMany(r => r.Entries)).Where(e => e.Card is not null).ToList();
        foreach (var card in Enum.GetValues<RoyaleCard>())
        {
            var mine = plays.Where(e => e.Card == card).ToList();
            var applied = mine.Where(e => e.Outcome == RoyaleOutcome.Applied).ToList();
            var (wins, players, fair) = WinRate(all, (match, seat) => match.Ledger.Any(r => r.Entries.Any(e => e.Seat == seat && e.Card == card)));
            report.AppendLine(Invariant($"| {card} | {Percent(mine.Count, plays.Count)} | {Percent(applied.Count, mine.Count)} | {(applied.Count == 0 ? 0 : applied.Average(e => e.Amount)):0.0} | {Percent(wins, players)} | {Percent(fair, players)} |"));
        }

        report.AppendLine("\n### Cards: version 1 against version 2 (engaged, all table sizes)\n");
        report.AppendLine("| Card | Mean MON when successful, v1 | v2 | Win rate of players who played it, v1 | v2 |");
        report.AppendLine("|---|---|---|---|---|");
        var v1 = new RoyaleSimulator(RoyaleRulesV1.Rules);
        var before = TableSizes.SelectMany(n => RoyaleScenarios.PlayMany(v1, Matches, seed => RoyaleScenarios.Engaged(seed, n))).ToList();
        foreach (var card in Enum.GetValues<RoyaleCard>())
        {
            var (oldMon, oldWins) = CardRow(before, card);
            var (newMon, newWins) = CardRow(all, card);
            report.AppendLine($"| {card} | {oldMon} | {newMon} | {oldWins} | {newWins} |");
        }

        report.AppendLine("\n### Abilities, styles and seats (engaged, all table sizes)\n");
        report.AppendLine("| Group | Win rate | Fair share |");
        report.AppendLine("|---|---|---|");
        foreach (var ability in RoyaleScenarios.Abilities)
        {
            var (wins, players, fair) = WinRate(all, (match, seat) => match.Players.Single(p => p.Seat == seat).Player.Ability == ability);
            report.AppendLine($"| Ability {ability} | {Percent(wins, players)} | {Percent(fair, players)} |");
        }

        foreach (var style in Enum.GetValues<RoyaleStyle>())
        {
            var (wins, players, fair) = WinRate(all, (match, seat) => match.Players.Single(p => p.Seat == seat).Player.Style == style);
            report.AppendLine($"| Style {style} | {Percent(wins, players)} | {Percent(fair, players)} |");
        }

        foreach (var n in TableSizes)
        {
            foreach (var seat in new[] { 1, n })
            {
                var (wins, players, fair) = WinRate(engaged[n], (_, s) => s == seat);
                report.AppendLine(Invariant($"| Seat {seat} of {n} | {Percent(wins, players)} | {Percent(fair, players)} |"));
            }
        }

        report.AppendLine("\n### Without abilities (engaged)\n");
        report.AppendLine("| Table | Median final MON | Winner's median | Shared first |");
        report.AppendLine("|---|---|---|---|");
        foreach (var n in TableSizes)
        {
            var plain = RoyaleScenarios.PlayMany(simulator, Matches, seed => RoyaleScenarios.Engaged(seed, n, abilities: false));
            var mon = plain.SelectMany(m => m.Players.Select(p => p.Mon)).Order().ToArray();
            var winners = plain.Select(m => m.Players.Max(p => p.Mon)).Order().ToArray();
            report.AppendLine(Invariant($"| {n} players | {At(mon, 0.5)} | {At(winners, 0.5)} | {Percent(plain.Count(m => m.Players.Count(p => p.Rank == 1) > 1), plain.Count)} |"));
        }

        output.WriteLine(report.ToString());
    }

    /// <summary>A card's mean MON when successful and the win rate of the players who played it.</summary>
    private static (string Mon, string Wins) CardRow(IReadOnlyList<RoyaleSimMatch> matches, RoyaleCard card)
    {
        var applied = matches.SelectMany(m => m.Ledger.SelectMany(r => r.Entries)).Where(e => e.Card == card && e.Outcome == RoyaleOutcome.Applied).ToList();
        var (wins, players, _) = WinRate(matches, (match, seat) => match.Ledger.Any(r => r.Entries.Any(e => e.Seat == seat && e.Card == card)));
        return (Invariant($"{(applied.Count == 0 ? 0 : applied.Average(e => e.Amount)):0.0}"), Percent(wins, players));
    }

    private static string DurationRow(string label, IReadOnlyList<RoyaleSimMatch> matches)
    {
        var seconds = matches.Select(m => m.Duration.TotalSeconds).Order().ToArray();
        var rounds = matches.Sum(m => m.Rounds);
        var early = matches.Sum(m => m.EarlyRounds);
        return Invariant(
            $"| {label} | {Clock(At(seconds, 0.1))} | {Clock(At(seconds, 0.5))} | {Clock(At(seconds, 0.9))} | {Clock(seconds[^1])} | {Percent(early, rounds)} | {Percent(matches.Count(m => m.Ending == RoomStatus.Completed), matches.Count)} | {Percent(matches.Count(m => m.Ending == RoomStatus.Abandoned), matches.Count)} |");
    }

    /// <summary>Of the players a group holds, how many won, and how many a fair share (1 in the table size) would be.</summary>
    private static (int Wins, int Players, double Fair) WinRate(IEnumerable<RoyaleSimMatch> matches, Func<RoyaleSimMatch, int, bool> inGroup)
    {
        var (wins, players, fair) = (0, 0, 0.0);
        foreach (var match in matches)
        {
            foreach (var player in match.Players.Where(p => inGroup(match, p.Seat)))
            {
                players++;
                wins += player.Rank == 1 ? 1 : 0;
                fair += 1.0 / match.Players.Count;
            }
        }

        return (wins, players, fair);
    }

    private static T At<T>(T[] sorted, double quantile) => sorted[(int)Math.Min(sorted.Length - 1, Math.Floor(quantile * sorted.Length))];

    private static string Clock(double seconds) => Invariant($"{(int)seconds / 60}:{(int)seconds % 60:00}");

    private static string Percent(double part, double whole) => whole == 0 ? "–" : Invariant($"{100 * part / whole:0.0}%");

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
