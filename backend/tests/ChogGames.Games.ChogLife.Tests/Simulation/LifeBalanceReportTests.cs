using System.Globalization;
using System.Text;
using ChogGames.Games.ChogLife.Rules;
using Xunit.Abstractions;
using static ChogGames.Games.ChogLife.Tests.Simulation.LifeBalanceReport;

namespace ChogGames.Games.ChogLife.Tests.Simulation;

/// <summary>
/// STORY-044: prints the tables of <c>docs/balancing.md</c> from seeded simulations; run with
/// <c>CHOG_BALANCE_REPORT=1 dotnet test --filter LifeBalanceReportTests --logger "console;verbosity=detailed"</c>.
/// The seeds are fixed, so the same rules print the same tables.
/// </summary>
public class LifeBalanceReportTests(ITestOutputHelper output)
{
    private const int Matches = 2_000;

    private static readonly int[] TableSizes = [2, 4, 6];

    private static readonly (string Label, SimPace Pace)[] Paces =
    [
        ("Engaged (2–8 s an action)", SimPace.Engaged),
        ("Slow (8–20 s an action)", new SimPace(TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(25))),
    ];

    [BalanceReportFact]
    public void Print_the_balance_report()
    {
        var report = new StringBuilder();
        var v2 = new LifeSimulator(LifeRulesV2.Rules);

        report.AppendLine("### Duration and endings (version 2)\n");
        report.AppendLine("| Table | p10 min | Median min | p90 min | Max min | Avg turns | Finish | Time up | Abandoned |");
        report.AppendLine("|---|---|---|---|---|---|---|---|---|");
        foreach (var count in TableSizes)
        {
            foreach (var (label, pace) in Paces)
            {
                report.AppendLine("| " + DurationRow($"{count} players, {label}", SimScenarios.PlayMany(v2, Matches, seed => SimScenarios.Engaged(seed, count), pace)) + " |");
            }

            report.AppendLine("| " + DurationRow($"{count} players, 30% of turns AFK", SimScenarios.PlayMany(
                v2, Matches, seed => SimScenarios.Engaged(seed, count).Select(p => p with { AfkChance = 0.3 }).ToArray())) + " |");
            report.AppendLine("| " + DurationRow($"{count} players, messy", SimScenarios.PlayMany(v2, Matches, seed => SimScenarios.Messy(seed, count))) + " |");
        }

        report.AppendLine("\n### Finish bonus: version 1 (100 MON) against version 2 (50 MON)\n");
        report.AppendLine("| Table | First finisher wins, v1 | v2 | Seat 1 wins, v1 | v2 | Fair share |");
        report.AppendLine("|---|---|---|---|---|---|");
        var v1 = new LifeSimulator(LifeRulesV1.Rules);
        foreach (var count in TableSizes)
        {
            var before = SimScenarios.PlayMany(v1, Matches, seed => SimScenarios.Engaged(seed, count, abilities: false));
            var after = SimScenarios.PlayMany(v2, Matches, seed => SimScenarios.Engaged(seed, count, abilities: false));
            report.AppendLine(CultureInfo.InvariantCulture, $"| {count} players | {Wins(before, p => p.Place == 1)} | {Wins(after, p => p.Place == 1)} | {Wins(before, p => p.Seat == 1)} | {Wins(after, p => p.Seat == 1)} | {Percent(100.0 / count)} |");
        }

        foreach (var count in TableSizes)
        {
            var matches = SimScenarios.PlayMany(v2, Matches, seed => SimScenarios.Engaged(seed, count));
            report.AppendLine(CultureInfo.InvariantCulture, $"\n### {count} players by lane strategy (version 2, fair share {Percent(100.0 / count)})\n");
            report.Append(OutcomeTable(matches, p => p.Lanes.ToString()));
            report.AppendLine(CultureInfo.InvariantCulture, $"\n### {count} players by ability (version 2)\n");
            report.Append(OutcomeTable(matches, p => p.Ability ?? "none"));
            var players = matches.SelectMany(m => m.Players).Where(p => p.Result is not null).ToArray();
            var scores = players.Select(p => p.Result!.Value.GetProperty("degenScore").GetInt32()).ToArray();
            report.AppendLine(CultureInfo.InvariantCulture,
                $"\nDegen Score: average {scores.Average():0}, 100 for {Percent(Share(scores.Count(s => s == 100), scores.Length))}; " +
                $"engaged turns timed out: {matches.Sum(m => m.TimedOutTurns)} of {matches.Sum(m => m.Turns)}.");
        }

        output.WriteLine(report.ToString());
    }

    private static string Wins(IReadOnlyList<SimMatch> matches, Func<SimPlayerResult, bool> which)
    {
        var group = matches.SelectMany(m => m.Players).Where(which).ToArray();
        return Percent(Share(group.Count(p => p.Rank == 1), group.Length));
    }
}
