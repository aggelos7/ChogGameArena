using ChogGames.Core.Players;
using ChogGames.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ChogGames.Infrastructure.Identity;

public sealed record IssuedGuestSession(Player Player, string Token, DateTimeOffset ExpiresAt);

public sealed record ActiveGuestSession(Player Player, DateTimeOffset ExpiresAt, bool Renewed);

public sealed class GuestSessionService(ChogGamesDbContext dbContext, TimeProvider timeProvider, IOptions<GuestSessionOptions> options)
{
    private GuestSessionOptions Options => options.Value;

    /// <summary>Creates a new guest player and session. The plain token is returned once and never stored.</summary>
    public async Task<IssuedGuestSession> CreateAsync(PlayerDisplayName? displayName, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var player = Player.CreateGuest(displayName ?? PlayerDisplayName.GenerateGuestName(), now);
        var token = SessionToken.Generate();
        SessionToken.TryHash(token, out var tokenHash);
        var session = GuestSession.Create(player, tokenHash, now, Options.Lifetime);

        dbContext.Players.Add(player);
        dbContext.GuestSessions.Add(session);
        await dbContext.SaveChangesAsync(cancellationToken);

        return new IssuedGuestSession(player, token, session.ExpiresAt);
    }

    /// <summary>
    /// Adds a new session for <paramref name="player"/> to the unit of work without saving, so callers can
    /// rotate sessions atomically with other changes (e.g. wallet sign-in).
    /// </summary>
    public IssuedGuestSession StartSession(Player player, DateTimeOffset now)
    {
        var token = SessionToken.Generate();
        SessionToken.TryHash(token, out var tokenHash);
        var session = GuestSession.Create(player, tokenHash, now, Options.Lifetime);
        dbContext.GuestSessions.Add(session);
        return new IssuedGuestSession(player, token, session.ExpiresAt);
    }

    /// <summary>Marks the session for <paramref name="token"/> revoked in the unit of work without saving.</summary>
    public async Task RevokeAsync(string? token, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!SessionToken.TryHash(token, out var tokenHash))
        {
            return;
        }

        var session = await dbContext.GuestSessions.SingleOrDefaultAsync(s => s.TokenHash == tokenHash, cancellationToken);
        session?.Revoke(now);
    }

    /// <summary>Ends the session for <paramref name="token"/> immediately. Unknown tokens are ignored.</summary>
    public async Task SignOutAsync(string? token, CancellationToken cancellationToken)
    {
        await RevokeAsync(token, timeProvider.GetUtcNow(), cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Resolves an active session from a client-supplied token, renewing it when due.</summary>
    public async Task<ActiveGuestSession?> AuthenticateAsync(string? token, CancellationToken cancellationToken)
    {
        if (!SessionToken.TryHash(token, out var tokenHash))
        {
            return null;
        }

        var session = await dbContext.GuestSessions
            .Include(s => s.Player)
            .SingleOrDefaultAsync(s => s.TokenHash == tokenHash, cancellationToken);

        var now = timeProvider.GetUtcNow();
        if (session is null || !session.IsActive(now))
        {
            return null;
        }

        var renewed = session.TryRenew(now, Options.Lifetime, Options.RenewInterval);
        if (renewed)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return new ActiveGuestSession(session.Player, session.ExpiresAt, renewed);
    }
}
