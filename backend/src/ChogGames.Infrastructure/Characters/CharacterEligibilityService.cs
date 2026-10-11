using ChogGames.Core.Nfts;
using ChogGames.Core.Players;
using ChogGames.Core.Wallets;
using Microsoft.Extensions.Logging;

namespace ChogGames.Infrastructure.Characters;

public enum CharacterEligibilityStatus
{
    /// <summary>The character may be used; <see cref="CharacterEligibility.Snapshot"/> is set.</summary>
    Eligible,

    /// <summary>The selected NFT is not owned by the player's wallet now. The player must choose again.</summary>
    NftNotOwned,

    /// <summary>Ownership of the selected NFT can't be read right now. The match must not start with it (DEC-019).</summary>
    OwnershipUnavailable,
}

public sealed record CharacterEligibility(CharacterEligibilityStatus Status, CharacterSnapshot? Snapshot)
{
    public static CharacterEligibility NotOwned { get; } = new(CharacterEligibilityStatus.NftNotOwned, null);

    public static CharacterEligibility Unavailable { get; } = new(CharacterEligibilityStatus.OwnershipUnavailable, null);
}

/// <summary>
/// Checks a player's selected character right before a match starts and fixes it as a <see cref="CharacterSnapshot"/>
/// (STORY-018, NFT-008/NFT-009). NFT ownership is read live from the chain on every call, never from stored ownership,
/// the gallery or the browser. The player's selection is never changed here.
/// </summary>
public sealed class CharacterEligibilityService(INftProvider nfts, TimeProvider timeProvider, ILogger<CharacterEligibilityService> logger)
{
    public async Task<CharacterEligibility> SnapshotForMatchAsync(Player player, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        if (player.SelectedCharacterKind == CharacterKind.Standard)
        {
            // A standard Chog retired from the built-in list falls back to the default rather than blocking the match.
            var standard = DefaultAvatar.TryFind(player.SelectedCharacterId, out var found) ? found : DefaultAvatar.Classic;
            return new CharacterEligibility(CharacterEligibilityStatus.Eligible, CharacterSnapshot.Standard(standard, now));
        }

        if (!WalletAddress.TryParse(player.WalletAddress, out var wallet) || !NftTokenId.TryParse(player.SelectedCharacterId, out var tokenId))
        {
            return CharacterEligibility.NotOwned;
        }

        OwnedNft? nft;
        try
        {
            nft = await nfts.FindOwnedAsync(wallet, tokenId, cancellationToken);
        }
        catch (NftProviderUnavailableException)
        {
            logger.LogWarning("Selected NFT {TokenId} could not be verified for a match: ownership is unavailable.", tokenId);
            return CharacterEligibility.Unavailable;
        }

        return nft is null
            ? CharacterEligibility.NotOwned
            : new CharacterEligibility(CharacterEligibilityStatus.Eligible, CharacterSnapshot.Nft(wallet, nft, now));
    }
}
