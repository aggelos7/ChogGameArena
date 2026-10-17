namespace ChogGames.Core.Progression;

/// <summary>The time a leaderboard covers (STORY-059, DEC-064).</summary>
public enum LeaderboardPeriod
{
    AllTime,

    /// <summary>The current <see cref="Season"/>.</summary>
    Season,
}

/// <summary>A season: one UTC calendar month, from <see cref="StartsAt"/> up to but not including <see cref="EndsAt"/>.</summary>
public sealed record Season(DateTimeOffset StartsAt, DateTimeOffset EndsAt)
{
    public static Season Containing(DateTimeOffset moment)
    {
        var utc = moment.ToUniversalTime();
        var start = new DateTimeOffset(utc.Year, utc.Month, 1, 0, 0, 0, TimeSpan.Zero);
        return new Season(start, start.AddMonths(1));
    }
}
