using System.Security.Cryptography;
using ChogGames.Core.Players;
using ChogGames.Core.Wallets;

namespace ChogGames.Infrastructure.Wallets;

/// <summary>
/// A one-time sign-in challenge bound to the requesting player's session, one address, the configured domain and
/// chain, and an expiry. It is consumed by the first verification attempt, successful or not.
/// </summary>
public sealed class WalletChallenge
{
    public const int NonceLength = 32;
    public const int MaxMessageLength = 1024;

    public Guid Id { get; private set; }

    public Guid PlayerId { get; private set; }

    public string Address { get; private set; } = string.Empty;

    public long ChainId { get; private set; }

    public string Nonce { get; private set; } = string.Empty;

    public string Message { get; private set; } = string.Empty;

    public DateTimeOffset IssuedAt { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    public DateTimeOffset? ConsumedAt { get; private set; }

    private WalletChallenge()
    {
    }

    public static WalletChallenge Create(Player player, WalletAddress address, long chainId, WalletAuthOptions options, DateTimeOffset now)
    {
        // 128 random bits as 32 hex characters (EIP-4361 needs at least 8 alphanumerics).
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var expiresAt = now + options.ChallengeLifetime;

        return new WalletChallenge
        {
            Id = Guid.NewGuid(),
            PlayerId = player.Id,
            Address = address.Value,
            ChainId = chainId,
            Nonce = nonce,
            Message = SignInMessage.Create(options.Domain, options.Uri, address, chainId, nonce, now, expiresAt),
            IssuedAt = now,
            ExpiresAt = expiresAt,
        };
    }
}
