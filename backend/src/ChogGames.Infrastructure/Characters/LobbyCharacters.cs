using System.Numerics;
using ChogGames.Core.Nfts;
using ChogGames.Core.Players;
using ChogGames.Infrastructure.Abilities;
using ChogGames.Infrastructure.Nfts;
using ChogGames.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ChogGames.Infrastructure.Characters;

/// <summary>
/// The player's selected Chog as stored, for showing and checking their ability pool in the lobby (STORY-033, DEC-035).
/// Nothing is read from the chain. Traits are included only when trait-based abilities are on (STORY-061, DEC-066):
/// those the metadata cache stored for the token while the player's wallet held it, or the fixtures' under the mock
/// provider. At start the pool is checked again with the live snapshot (STORY-018), which is what the match keeps.
/// </summary>
public sealed class LobbyCharacters(
    ChogGamesDbContext db,
    IOptions<TraitAbilityOptions> traitOptions,
    IOptions<NftOptions> nftOptions,
    TimeProvider timeProvider)
{
    public async Task<CharacterSnapshot> ForAsync(Player player, CancellationToken cancellationToken) =>
        (await ForEachAsync([player], cancellationToken))[player.Id];

    /// <summary>Each player's lobby Chog, with one read of the cache for all of them.</summary>
    public async Task<IReadOnlyDictionary<Guid, CharacterSnapshot>> ForEachAsync(
        IReadOnlyCollection<Player> players, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var traits = traitOptions.Value.Enabled
            ? await StoredTraitsAsync([.. players.Where(p => p.SelectedCharacterKind == CharacterKind.Nft)], cancellationToken)
            : [];
        return players.DistinctBy(p => p.Id).ToDictionary(p => p.Id, p => For(p, traits.GetValueOrDefault(p.Id, []), now));
    }

    private static CharacterSnapshot For(Player player, IReadOnlyList<NftTrait> traits, DateTimeOffset now)
    {
        if (player.SelectedCharacterKind == CharacterKind.Nft)
        {
            return CharacterSnapshot.Restore(
                CharacterKind.Nft, player.SelectedCharacterId, $"Chog #{player.SelectedCharacterId}", null, traits, player.WalletAddress, now);
        }

        var standard = DefaultAvatar.TryFind(player.SelectedCharacterId, out var found) ? found : DefaultAvatar.Classic;
        return CharacterSnapshot.Standard(standard, now);
    }

    private async Task<Dictionary<Guid, IReadOnlyList<NftTrait>>> StoredTraitsAsync(
        IReadOnlyList<Player> holders, CancellationToken cancellationToken)
    {
        var selected = new List<(Player Player, BigInteger TokenId)>();
        foreach (var player in holders)
        {
            if (player.WalletAddress is not null && NftTokenId.TryParse(player.SelectedCharacterId, out var tokenId))
            {
                selected.Add((player, tokenId.Value));
            }
        }

        if (selected.Count == 0)
        {
            return [];
        }

        var nft = nftOptions.Value;
        if (nft.Provider == NftProviderKind.Mock)
        {
            return selected.ToDictionary(
                s => s.Player.Id,
                s => MockNftFixtures.All.FirstOrDefault(n => n.TokenId.Value == s.TokenId)?.Traits ?? []);
        }

        if (nft.Provider != NftProviderKind.Monad)
        {
            return [];
        }

        var chainId = nft.Monad.ChainId;
        var contract = nft.Monad.ContractAddress.ToLowerInvariant();
        var tokenIds = selected.Select(s => s.TokenId).Distinct().ToList();
        var rows = await db.ChogNfts.AsNoTracking()
            .Where(n => n.ChainId == chainId && n.ContractAddress == contract && tokenIds.Contains(n.TokenId))
            .ToListAsync(cancellationToken);
        return selected.ToDictionary(
            s => s.Player.Id,
            s => rows.FirstOrDefault(r => r.TokenId == s.TokenId && r.OwnerAddress == s.Player.WalletAddress) is { } row
                ? (IReadOnlyList<NftTrait>)[.. row.Traits.OrderBy(t => t.Position).Select(t => new NftTrait(t.TraitType, t.TraitValue))]
                : []);
    }
}
