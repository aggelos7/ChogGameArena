namespace ChogGames.Games.ChogLife.Tests.Simulation;

/// <summary>
/// Runs only when <c>CHOG_BALANCE_REPORT</c> is set: the full balance report of STORY-044 plays tens of thousands of
/// matches and prints the tables of <c>docs/balancing.md</c>, so it is off in CI.
/// </summary>
public sealed class BalanceReportFactAttribute : FactAttribute
{
    public const string EnvironmentVariable = "CHOG_BALANCE_REPORT";

    public BalanceReportFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(EnvironmentVariable)))
        {
            Skip = $"Set {EnvironmentVariable}=1 to print the Chog Life balance report.";
        }
    }
}
