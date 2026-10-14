using System.Globalization;
using System.Text;

namespace ChogGames.Games.ChogLife.Tests.Simulation;

/// <summary>Summaries of many simulated matches, as the tables in <c>docs/balancing.md</c> show them (STORY-044).</summary>
public static class LifeBalanceReport
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public static TimeSpan Percentile(IEnumerable<TimeSpan> values, double percentile)
    {
        var sorted = values.OrderBy(v => v).ToArray();
        return sorted[(int)Math.Min(sorted.Length - 1, Math.Floor(percentile / 100 * sorted.Length))];
    }

    public static double Share(int count, int total) => total == 0 ? 0 : 100.0 * count / total;

    public static string Minutes(TimeSpan value) => value.TotalMinutes.ToString("0.0", Invariant);

    public static string Percent(double value) => value.ToString("0", Invariant) + "%";

    /// <summary>Duration, turns and endings of a set of matches, as one table row.</summary>
    public static string DurationRow(string label, IReadOnlyList<SimMatch> matches)
    {
        var durations = matches.Select(m => m.Duration).ToArray();
        return string.Join(" | ",
            label,
            Minutes(Percentile(durations, 10)),
            Minutes(Percentile(durations, 50)),
            Minutes(Percentile(durations, 90)),
            Minutes(durations.Max()),
            matches.Average(m => m.Turns).ToString("0", Invariant),
            Percent(Share(matches.Count(m => m.Ending == SimEnding.Finish), matches.Count)),
            Percent(Share(matches.Count(m => m.Ending == SimEnding.TimeUp), matches.Count)),
            Percent(Share(matches.Count(m => m.Ending == SimEnding.Abandoned), matches.Count)));
    }

    /// <summary>Win share, average final portfolio, finish place and debt per kind of player, over completed matches.</summary>
    public static string OutcomeTable(IReadOnlyList<SimMatch> matches, Func<SimPlayer, string> kind)
    {
        var players = matches
            .Where(m => m.Ending != SimEnding.Abandoned)
            .SelectMany(m => m.Players.Where(p => !p.Left).Select(p => (Match: m, Player: p)))
            .ToArray();
        var builder = new StringBuilder("| Player | Wins (shared count) | Avg portfolio | Median | Debt at end | Below start |\n|---|---|---|---|---|---|\n");
        foreach (var group in players.GroupBy(p => kind(p.Player.Player)).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var portfolios = group.Select(p => p.Player.Portfolio).OrderBy(v => v).ToArray();
            builder.Append(Invariant, $"| {group.Key} | {Percent(Share(group.Count(p => p.Player.Rank == 1), group.Count()))} | ")
                .Append(Invariant, $"{portfolios.Average():0} | {portfolios[portfolios.Length / 2]} | ")
                .Append(Percent(Share(group.Count(p => p.Player.Result!.Value.GetProperty("liabilities").GetInt32() > 0), group.Count())))
                .Append(" | ")
                .Append(Percent(Share(group.Count(p => p.Player.Portfolio < 100), group.Count())))
                .Append(" |\n");
        }

        return builder.ToString();
    }
}
