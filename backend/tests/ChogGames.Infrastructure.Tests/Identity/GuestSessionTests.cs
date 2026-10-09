using ChogGames.Core.Players;
using ChogGames.Infrastructure.Identity;

namespace ChogGames.Infrastructure.Tests.Identity;

public class GuestSessionTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Lifetime = TimeSpan.FromDays(90);
    private static readonly TimeSpan RenewInterval = TimeSpan.FromDays(1);

    private static GuestSession NewSession()
    {
        PlayerDisplayName.TryCreate("Bob", out var name);
        return GuestSession.Create(Player.CreateGuest(name!, Start), new byte[32], Start, Lifetime);
    }

    [Fact]
    public void New_session_is_active_until_it_expires()
    {
        var session = NewSession();

        Assert.True(session.IsActive(Start));
        Assert.True(session.IsActive(Start + Lifetime - TimeSpan.FromSeconds(1)));
        Assert.False(session.IsActive(Start + Lifetime));
    }

    [Fact]
    public void Renewal_happens_at_most_once_per_interval_and_slides_expiry()
    {
        var session = NewSession();

        Assert.False(session.TryRenew(Start + TimeSpan.FromHours(23), Lifetime, RenewInterval));
        Assert.Equal(Start + Lifetime, session.ExpiresAt);

        var later = Start + TimeSpan.FromDays(10);
        Assert.True(session.TryRenew(later, Lifetime, RenewInterval));
        Assert.Equal(later + Lifetime, session.ExpiresAt);
        Assert.False(session.TryRenew(later + TimeSpan.FromHours(1), Lifetime, RenewInterval));
    }

    [Fact]
    public void Revoked_session_is_inactive_and_keeps_first_revocation_time()
    {
        var session = NewSession();

        session.Revoke(Start + TimeSpan.FromDays(1));
        session.Revoke(Start + TimeSpan.FromDays(2));

        Assert.False(session.IsActive(Start + TimeSpan.FromDays(3)));
        Assert.Equal(Start + TimeSpan.FromDays(1), session.RevokedAt);
    }
}
