using ChogGames.Core.Players;

namespace ChogGames.Infrastructure.Identity;

/// <summary>
/// Durable anonymous session for a guest player. Only the SHA-256 hash of the bearer token is
/// stored, so a database leak does not reveal usable session tokens.
/// </summary>
public sealed class GuestSession
{
    public Guid Id { get; private set; }

    public Guid PlayerId { get; private set; }

    public Player Player { get; private set; } = null!;

    public byte[] TokenHash { get; private set; } = [];

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    public DateTimeOffset LastRenewedAt { get; private set; }

    public DateTimeOffset? RevokedAt { get; private set; }

    private GuestSession()
    {
    }

    public static GuestSession Create(Player player, byte[] tokenHash, DateTimeOffset now, TimeSpan lifetime) => new()
    {
        Id = Guid.NewGuid(),
        PlayerId = player.Id,
        Player = player,
        TokenHash = tokenHash,
        CreatedAt = now,
        ExpiresAt = now + lifetime,
        LastRenewedAt = now,
    };

    public bool IsActive(DateTimeOffset now) => RevokedAt is null && ExpiresAt > now;

    /// <summary>Sliding expiration, persisted at most once per <paramref name="renewInterval"/>.</summary>
    public bool TryRenew(DateTimeOffset now, TimeSpan lifetime, TimeSpan renewInterval)
    {
        if (now - LastRenewedAt < renewInterval)
        {
            return false;
        }

        ExpiresAt = now + lifetime;
        LastRenewedAt = now;
        return true;
    }

    public void Revoke(DateTimeOffset now) => RevokedAt ??= now;
}
