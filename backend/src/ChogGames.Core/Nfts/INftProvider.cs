using ChogGames.Core.Wallets;

namespace ChogGames.Core.Nfts;

/// <summary>
/// Reads ownership and normalized metadata of the official Chog collection (ADR-006). Callers pass only a
/// wallet address the server has verified (STORY-013), never one taken from a request.
/// </summary>
public interface INftProvider
{
    /// <summary>All NFTs the wallet owns, ordered by token id; empty when it owns none.</summary>
    /// <exception cref="NftProviderUnavailableException">Ownership could not be read; nothing should be assumed.</exception>
    Task<IReadOnlyList<OwnedNft>> GetOwnedAsync(WalletAddress owner, CancellationToken cancellationToken);

    /// <summary>The token when the wallet owns it right now; null when it does not or the token does not exist.</summary>
    /// <exception cref="NftProviderUnavailableException">Ownership could not be read; treat as not verified.</exception>
    Task<OwnedNft?> FindOwnedAsync(WalletAddress owner, NftTokenId tokenId, CancellationToken cancellationToken);
}

/// <summary>The ownership source (RPC, indexer) failed. Never treated as "owns nothing" or "owns it".</summary>
public sealed class NftProviderUnavailableException(string message, Exception? innerException = null)
    : Exception(message, innerException);
