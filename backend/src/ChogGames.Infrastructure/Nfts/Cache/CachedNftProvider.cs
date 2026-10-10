using System.Numerics;
using ChogGames.Core.Nfts;
using ChogGames.Core.Wallets;
using ChogGames.Infrastructure.Nfts.Metadata;
using ChogGames.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ChogGames.Infrastructure.Nfts.Cache;

/// <summary>
/// Stores ownership and normalized metadata of the chain provider's answers (STORY-016, NFT-003, NFT-010).
/// <list type="bullet">
/// <item>Ownership always comes from <paramref name="inner"/> and is then stored: tokens that left the wallet
/// lose their stored owner. Only listing may fall back to a recent stored scan during an outage; single-token
/// checks never do.</item>
/// <item>Missing or stale metadata is fetched within a per-request time budget; tokens not reached keep their
/// stored metadata (or a placeholder) and are fetched by a later request. Bad metadata never fails a request.</item>
/// </list>
/// </summary>
public sealed class CachedNftProvider(
    INftProvider inner,
    INftMetadataSource metadataSource,
    ChogGamesDbContext db,
    IOptions<NftOptions> options,
    TimeProvider timeProvider,
    ILogger<CachedNftProvider> logger) : INftProvider
{
    private long ChainId => options.Value.Monad.ChainId;

    private string Contract => options.Value.Monad.ContractAddress.ToLowerInvariant();

    private NftCacheOptions Cache => options.Value.Cache;

    public async Task<IReadOnlyList<OwnedNft>> GetOwnedAsync(WalletAddress owner, CancellationToken cancellationToken)
    {
        IReadOnlyList<OwnedNft> live;
        try
        {
            live = await inner.GetOwnedAsync(owner, cancellationToken);
        }
        catch (NftProviderUnavailableException)
        {
            return await GetStoredOwnedAsync(owner, cancellationToken)
                ?? throw new NftProviderUnavailableException("NFT ownership could not be read and no recent stored scan exists.");
        }

        var now = timeProvider.GetUtcNow();
        var tokenIds = live.Select(nft => nft.TokenId.Value).ToArray();
        await StoreOwnershipAsync(owner, tokenIds, now, cancellationToken);

        var rows = await LoadAsync(tokenIds, cancellationToken);
        rows = await RefreshMetadataAsync(rows, now, cancellationToken);
        return rows.Select(ToOwnedNft).ToList();
    }

    public async Task<OwnedNft?> FindOwnedAsync(WalletAddress owner, NftTokenId tokenId, CancellationToken cancellationToken)
    {
        // Always a live check (NFT-008); stored ownership is never used here.
        var live = await inner.FindOwnedAsync(owner, tokenId, cancellationToken);
        var now = timeProvider.GetUtcNow();
        if (live is null)
        {
            // Stale owner: the wallet no longer holds it.
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE "ChogNfts" SET "OwnerAddress" = NULL, "OwnerRefreshedAt" = {now}
                WHERE "ChainId" = {ChainId} AND "ContractAddress" = {Contract} AND "TokenId" = {tokenId.Value}
                  AND "OwnerAddress" = {owner.Value} AND "OwnerRefreshedAt" <= {now}
                """, cancellationToken);
            return null;
        }

        BigInteger[] tokenIds = [tokenId.Value];
        await UpsertOwnerAsync(owner, tokenIds, now, cancellationToken);
        var rows = await RefreshMetadataAsync(await LoadAsync(tokenIds, cancellationToken), now, cancellationToken);
        return rows.Select(ToOwnedNft).Single();
    }

    /// <summary>The last stored scan of this wallet if it is recent enough, else null.</summary>
    private async Task<IReadOnlyList<OwnedNft>?> GetStoredOwnedAsync(WalletAddress owner, CancellationToken cancellationToken)
    {
        var scan = await db.NftOwnershipScans.AsNoTracking().SingleOrDefaultAsync(
            s => s.ChainId == ChainId && s.ContractAddress == Contract && s.WalletAddress == owner.Value, cancellationToken);
        if (scan is null || timeProvider.GetUtcNow() - scan.ScannedAt > Cache.OwnershipFallback)
        {
            return null;
        }

        logger.LogWarning("NFT ownership is unavailable; listing a wallet from its stored scan of {ScannedAt:O}.", scan.ScannedAt);
        var rows = await db.ChogNfts.AsNoTracking()
            .Where(n => n.ChainId == ChainId && n.ContractAddress == Contract && n.OwnerAddress == owner.Value)
            .ToListAsync(cancellationToken);
        return rows.OrderBy(n => n.TokenId).Select(ToOwnedNft).ToList();
    }

    private async Task StoreOwnershipAsync(WalletAddress owner, BigInteger[] tokenIds, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await UpsertOwnerAsync(owner, tokenIds, now, cancellationToken);

        var owned = tokenIds.Select(id => id.ToString()).ToArray();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "ChogNfts" SET "OwnerAddress" = NULL, "OwnerRefreshedAt" = {now}
            WHERE "ChainId" = {ChainId} AND "ContractAddress" = {Contract} AND "OwnerAddress" = {owner.Value}
              AND "OwnerRefreshedAt" <= {now} AND NOT ("TokenId" = ANY ({owned}::numeric[]))
            """, cancellationToken);

        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "NftOwnershipScans" ("ChainId", "ContractAddress", "WalletAddress", "ScannedAt")
            VALUES ({ChainId}, {Contract}, {owner.Value}, {now})
            ON CONFLICT ("ChainId", "ContractAddress", "WalletAddress")
            DO UPDATE SET "ScannedAt" = GREATEST("NftOwnershipScans"."ScannedAt", excluded."ScannedAt")
            """, cancellationToken);

        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>Records <paramref name="owner"/> as the owner of the tokens, adding rows for tokens seen first.</summary>
    private Task UpsertOwnerAsync(WalletAddress owner, BigInteger[] tokenIds, DateTimeOffset now, CancellationToken cancellationToken)
    {
        // Older writes never overwrite newer ones, so concurrent requests settle on the latest answer.
        var ids = tokenIds.Select(id => id.ToString()).ToArray();
        var pending = nameof(NftMetadataStatus.Pending);
        return db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "ChogNfts" ("ChainId", "ContractAddress", "TokenId", "OwnerAddress", "OwnerRefreshedAt", "MetadataStatus")
            SELECT {ChainId}, {Contract}, id::numeric, {owner.Value}, {now}, {pending} FROM unnest({ids}::text[]) AS id
            ON CONFLICT ("ChainId", "ContractAddress", "TokenId")
            DO UPDATE SET "OwnerAddress" = excluded."OwnerAddress", "OwnerRefreshedAt" = excluded."OwnerRefreshedAt"
            WHERE "ChogNfts"."OwnerRefreshedAt" IS NULL OR "ChogNfts"."OwnerRefreshedAt" <= excluded."OwnerRefreshedAt"
            """, cancellationToken);
    }

    private async Task<List<ChogNft>> LoadAsync(BigInteger[] tokenIds, CancellationToken cancellationToken)
    {
        var rows = await db.ChogNfts
            .Where(n => n.ChainId == ChainId && n.ContractAddress == Contract && tokenIds.Contains(n.TokenId))
            .ToListAsync(cancellationToken);
        return [.. rows.OrderBy(n => n.TokenId)];
    }

    /// <summary>Fetches metadata for tokens that need it, within the time budget, and stores what was read.</summary>
    private async Task<List<ChogNft>> RefreshMetadataAsync(List<ChogNft> rows, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var due = rows.Where(row => row.NeedsMetadata(now, Cache)).ToList();
        if (due.Count == 0 || Cache.MetadataFetchBudget <= TimeSpan.Zero)
        {
            return rows;
        }

        var results = new System.Collections.Concurrent.ConcurrentDictionary<BigInteger, NftMetadata>();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(Cache.MetadataFetchBudget);
        try
        {
            await Parallel.ForEachAsync(
                due,
                new ParallelOptions { MaxDegreeOfParallelism = Cache.MaxConcurrentMetadataFetches, CancellationToken = budget.Token },
                async (row, token) =>
                {
                    NftTokenId.TryParse(row.TokenId.ToString(), out var tokenId);
                    try
                    {
                        results[row.TokenId] = await metadataSource.FetchAsync(tokenId, token);
                    }
                    catch (Exception error) when (error is not OperationCanceledException)
                    {
                        // Bad metadata must never fail the request; treat it as an outage and retry later.
                        logger.LogError(error, "Reading metadata of token {TokenId} failed unexpectedly.", tokenId);
                        results[row.TokenId] = NftMetadata.Unavailable();
                    }
                });
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogInformation("NFT metadata budget used up; {Fetched} of {Due} tokens fetched this request.", results.Count, due.Count);
        }

        if (results.IsEmpty)
        {
            return rows;
        }

        foreach (var row in due)
        {
            if (results.TryGetValue(row.TokenId, out var metadata))
            {
                row.ApplyMetadata(metadata, now);
            }
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return rows;
        }
        catch (DbUpdateConcurrencyException)
        {
            // Another request updated these tokens meanwhile; show what is stored now.
            db.ChangeTracker.Clear();
            return await LoadAsync([.. rows.Select(r => r.TokenId)], cancellationToken);
        }
    }

    private OwnedNft ToOwnedNft(ChogNft row)
    {
        NftTokenId.TryParse(row.TokenId.ToString(), out var tokenId);
        var image = IpfsUri.TryParse(row.ImageUri, out var imageUri) && options.Value.Ipfs.Gateways is [var gateway, ..]
            ? imageUri.ToGatewayUrl(gateway)
            : null;
        return new OwnedNft(
            tokenId,
            row.Name ?? $"Chog #{tokenId}",
            image,
            row.Traits.OrderBy(t => t.Position).Select(t => new NftTrait(t.TraitType, t.TraitValue)).ToList());
    }
}
