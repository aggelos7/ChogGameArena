using ChogGames.Core.Players;
using ChogGames.Core.Wallets;
using ChogGames.Infrastructure.Identity;
using ChogGames.Infrastructure.Monitoring;
using ChogGames.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace ChogGames.Infrastructure.Wallets;

public enum WalletSignInFailure
{
    /// <summary>Unknown, expired, already used, or issued to another session.</summary>
    ChallengeUnavailable,

    /// <summary>Malformed, or not made by the challenged address over the issued message.</summary>
    InvalidSignature,
}

public sealed record WalletSignInResult(IssuedGuestSession? Session, WalletSignInFailure? Failure)
{
    public static WalletSignInResult Failed(WalletSignInFailure failure) => new(null, failure);
}

/// <summary>
/// Challenge-sign wallet authentication (SUITE-003, ADR-005). An address on its own never authenticates:
/// only a valid signature over a fresh, unexpired, unused challenge that this server issued to this session does.
/// </summary>
public sealed class WalletSignInService(
    ChogGamesDbContext dbContext,
    GuestSessionService sessions,
    TimeProvider timeProvider,
    IOptions<WalletAuthOptions> options,
    ChogGamesMetrics metrics)
{
    public async Task<WalletChallenge> CreateChallengeAsync(Guid playerId, WalletAddress address, long chainId, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var player = await dbContext.Players.SingleAsync(p => p.Id == playerId, cancellationToken);

        // Housekeeping: this player's spent or expired challenges are useless; drop them so the table stays small.
        await dbContext.WalletChallenges
            .Where(c => c.PlayerId == playerId && (c.ConsumedAt != null || c.ExpiresAt <= now))
            .ExecuteDeleteAsync(cancellationToken);

        var challenge = WalletChallenge.Create(player, address, chainId, options.Value, now);
        dbContext.WalletChallenges.Add(challenge);
        await dbContext.SaveChangesAsync(cancellationToken);
        return challenge;
    }

    /// <summary>
    /// Verifies <paramref name="signature"/> for the caller's challenge. On success the wallet is bound (see DEC-015)
    /// and the caller's session is replaced by a new one; the old session token stops working.
    /// </summary>
    public async Task<WalletSignInResult> VerifyAsync(
        Guid playerId,
        string currentSessionToken,
        Guid challengeId,
        string? signature,
        CancellationToken cancellationToken)
    {
        var result = await VerifyChallengeAsync(playerId, currentSessionToken, challengeId, signature, cancellationToken);
        metrics.WalletSignIn(result.Failure);
        return result;
    }

    private async Task<WalletSignInResult> VerifyChallengeAsync(
        Guid playerId,
        string currentSessionToken,
        Guid challengeId,
        string? signature,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();

        // Consume first, atomically: a nonce can be used once, so replays and parallel attempts all fail,
        // and a failed attempt cannot be retried with other signatures.
        var consumed = await dbContext.WalletChallenges
            .Where(c => c.Id == challengeId && c.PlayerId == playerId && c.ConsumedAt == null && c.ExpiresAt > now)
            .ExecuteUpdateAsync(set => set.SetProperty(c => c.ConsumedAt, now), cancellationToken);
        if (consumed != 1)
        {
            return WalletSignInResult.Failed(WalletSignInFailure.ChallengeUnavailable);
        }

        var challenge = await dbContext.WalletChallenges.AsNoTracking().SingleAsync(c => c.Id == challengeId, cancellationToken);
        if (!EthereumSignature.TryRecoverPersonalSigner(challenge.Message, signature, out var signer) || signer.Value.Value != challenge.Address)
        {
            return WalletSignInResult.Failed(WalletSignInFailure.InvalidSignature);
        }

        try
        {
            return new WalletSignInResult(await BindAndRotateAsync(playerId, currentSessionToken, signer.Value, now, cancellationToken), null);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Another request bound this wallet at the same moment; that player now owns it, so sign in as them.
            dbContext.ChangeTracker.Clear();
            return new WalletSignInResult(await BindAndRotateAsync(playerId, currentSessionToken, signer.Value, now, cancellationToken), null);
        }
    }

    private async Task<IssuedGuestSession> BindAndRotateAsync(
        Guid playerId,
        string currentSessionToken,
        WalletAddress wallet,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var target = await dbContext.Players.SingleOrDefaultAsync(p => p.WalletAddress == wallet.Value, cancellationToken);
        if (target is null)
        {
            var current = await dbContext.Players.SingleAsync(p => p.Id == playerId, cancellationToken);
            if (current.Kind == PlayerKind.Guest)
            {
                current.BindWallet(wallet);
                target = current;
            }
            else
            {
                // The caller is signed in with a different wallet; one wallet per player, so this wallet gets its own.
                target = Player.CreateForWallet(wallet, PlayerDisplayName.GenerateGuestName(), now);
                dbContext.Players.Add(target);
            }
        }

        // Always rotate: the pre-sign-in token never carries the stronger identity (prevents session fixation).
        await sessions.RevokeAsync(currentSessionToken, now, cancellationToken);
        var issued = sessions.StartSession(target, now);
        await dbContext.SaveChangesAsync(cancellationToken);
        return issued;
    }
}
