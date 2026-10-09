namespace ChogGames.Infrastructure.Identity;

/// <summary>Guest session settings, bound from the <c>GuestSessions</c> configuration section.</summary>
public sealed class GuestSessionOptions
{
    public const string SectionName = "GuestSessions";

    /// <summary>How long a session stays valid without use. Each use (at most daily) extends it.</summary>
    public TimeSpan Lifetime { get; set; } = TimeSpan.FromDays(90);

    /// <summary>Minimum time between persisted renewals, to avoid a database write per request.</summary>
    public TimeSpan RenewInterval { get; set; } = TimeSpan.FromDays(1);

    /// <summary>
    /// Marks the cookie Secure (HTTPS only). Must stay true outside local development; the
    /// development and test configuration disable it because they use plain HTTP.
    /// </summary>
    public bool RequireSecureCookie { get; set; } = true;

    /// <summary>Maximum guest sessions one client IP may create per minute.</summary>
    public int CreatePerMinutePerClient { get; set; } = 10;
}
