using System.Collections.Concurrent;
using System.Numerics;
using ChogGames.Core.Nfts;
using ChogGames.Core.Wallets;
using ChogGames.Infrastructure.Nfts;
using ChogGames.Infrastructure.Nfts.Cache;
using ChogGames.Infrastructure.Nfts.Metadata;
using ChogGames.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ChogGames.Api.Tests.Nfts;

public class CachedNftProviderTests(MigratedPostgresDatabase database) : IClassFixture<MigratedPostgresDatabase>
{
    private const string Contract = "0x00000000000000000000000000000000000c0c00";
    private const string Gateway = "https://gateway.example/ipfs/";
    private const string MetadataCid = "bafybeid4ybujnscdipkno7ps5utqelp5ry5ryreqhay3zhgk3a6x4jruqu";
    private const string ImageCid = "bafybeihau2egcccbdxymeckmmbrqytbfor5gkt3en6d5fsbueymdbq7g6e";

    private static readonly WalletAddress Alice = Address("0x5aaeb6053f3e94c9b9a09f33669435e7ef1beaed");
    private static readonly WalletAddress Bob = Address("0xfb6916095ca1df60bb79ce92ce3ea74c37c5d359");
    private static readonly WalletAddress Carol = Address("0xdbf03b407c01e7cd3cbea99509d93f8dddc8c6fb");

    // A chain id per test keeps tests independent inside the shared database.
    private readonly long chainId = Random.Shared.NextInt64(1_000_000, long.MaxValue);
    private readonly FakeChain chain = new();
    private readonly FakeMetadata metadata = new();
    private readonly ManualTimeProvider clock = new(new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero));
    private readonly NftCacheOptions cache = new() { MetadataFetchBudget = TimeSpan.FromSeconds(5) };

    private static WalletAddress Address(string value)
    {
        Assert.True(WalletAddress.TryParse(value, out var address));
        return address;
    }

    private static IpfsUri Ipfs(string value)
    {
        Assert.True(IpfsUri.TryParse(value, out var uri));
        return uri;
    }

    private static NftMetadata Good(BigInteger id) => new(
        NftMetadataStatus.Ok,
        Ipfs($"ipfs://{MetadataCid}/{id + 1000}.json"),
        $"CHOG #{id + 1000}",
        Ipfs($"ipfs://{ImageCid}/{id + 1000}.webp"),
        [new NftTrait("Tier", "Rare"), new NftTrait("Brand New Trait", $"Value {id} ✨")]);

    private (CachedNftProvider Provider, ChogGamesDbContext Db) Create()
    {
        var db = database.CreateDbContext();
        var options = Options.Create(new NftOptions
        {
            Provider = NftProviderKind.Monad,
            Monad = { ChainId = chainId, ContractAddress = Contract },
            Ipfs = { Gateways = [Gateway] },
            Cache = cache,
        });
        return (new CachedNftProvider(chain, metadata, db, options, clock, NullLogger<CachedNftProvider>.Instance), db);
    }

    private async Task<IReadOnlyList<OwnedNft>> List(WalletAddress owner)
    {
        var (provider, db) = Create();
        await using (db)
        {
            return await provider.GetOwnedAsync(owner, CancellationToken.None);
        }
    }

    private async Task<OwnedNft?> Find(WalletAddress owner, ulong tokenId)
    {
        var (provider, db) = Create();
        await using (db)
        {
            return await provider.FindOwnedAsync(owner, NftTokenId.From(tokenId), CancellationToken.None);
        }
    }

    private async Task<List<ChogNft>> Rows()
    {
        await using var db = database.CreateDbContext();
        var rows = await db.ChogNfts.AsNoTracking().Where(n => n.ChainId == chainId).ToListAsync();
        return [.. rows.OrderBy(n => n.TokenId)];
    }

    private async Task<string?> StoredOwner(ulong tokenId) =>
        (await Rows()).Single(r => r.TokenId == tokenId).OwnerAddress;

    [PostgresFact]
    public async Task Listing_stores_owner_and_normalized_metadata()
    {
        chain.Holdings[Alice] = [3, 1];
        metadata.Respond = Good;

        var owned = await List(Alice);

        Assert.Equal(["1", "3"], owned.Select(n => n.TokenId.ToString()));
        var first = owned[0];
        Assert.Equal("CHOG #1001", first.Name);
        Assert.Equal(new Uri($"{Gateway}{ImageCid}/1001.webp"), first.ImageUrl);
        Assert.Equal(["Tier=Rare", "Brand New Trait=Value 1 ✨"], first.Traits.Select(t => $"{t.Type}={t.Value}"));

        var rows = await Rows();
        Assert.All(rows, row =>
        {
            Assert.Equal(Alice.Value, row.OwnerAddress);
            Assert.Equal(clock.Now, row.OwnerRefreshedAt);
            Assert.Equal(NftMetadataStatus.Ok, row.MetadataStatus);
            Assert.Equal(clock.Now, row.MetadataRefreshedAt);
            Assert.Equal(Contract, row.ContractAddress);
        });
        Assert.Equal($"ipfs://{MetadataCid}/1001.json", rows[0].MetadataUri);
        Assert.Equal($"ipfs://{ImageCid}/1001.webp", rows[0].ImageUri);
        Assert.Equal([0, 1], rows[0].Traits.OrderBy(t => t.Position).Select(t => t.Position));
    }

    [PostgresFact]
    public async Task Metadata_is_read_once_then_served_from_storage()
    {
        chain.Holdings[Alice] = [1, 2];
        metadata.Respond = Good;

        await List(Alice);
        clock.Advance(TimeSpan.FromDays(6));
        var again = await List(Alice);

        Assert.Equal(2, metadata.Calls.Count);
        Assert.Equal("CHOG #1002", again[1].Name);

        clock.Advance(TimeSpan.FromDays(1));
        await List(Alice);
        Assert.Equal(4, metadata.Calls.Count);
    }

    [PostgresFact]
    public async Task Refresh_updates_a_stale_owner()
    {
        chain.Holdings[Alice] = [1, 2];
        await List(Alice);

        // Token 2 moves to Bob.
        chain.Holdings[Alice] = [1];
        chain.Holdings[Bob] = [2];
        clock.Advance(TimeSpan.FromMinutes(1));

        Assert.Equal(["1"], (await List(Alice)).Select(n => n.TokenId.ToString()));
        Assert.Null(await StoredOwner(2));

        Assert.Equal(["2"], (await List(Bob)).Select(n => n.TokenId.ToString()));
        Assert.Equal(Bob.Value, await StoredOwner(2));
        Assert.Equal(Alice.Value, await StoredOwner(1));
    }

    [PostgresFact]
    public async Task Listing_a_new_owner_first_does_not_disturb_the_previous_owners_other_tokens()
    {
        chain.Holdings[Alice] = [1, 2];
        await List(Alice);
        chain.Holdings[Alice] = [1];
        chain.Holdings[Bob] = [2];
        clock.Advance(TimeSpan.FromMinutes(1));

        await List(Bob);

        Assert.Equal(Bob.Value, await StoredOwner(2));
        Assert.Equal(Alice.Value, await StoredOwner(1));
    }

    [PostgresFact]
    public async Task Single_token_check_is_live_and_updates_the_stored_owner()
    {
        chain.Holdings[Alice] = [1];
        await List(Alice);
        chain.Holdings[Alice] = [];
        chain.Holdings[Carol] = [1];

        Assert.Null(await Find(Alice, 1));
        Assert.Null(await StoredOwner(1));

        var owned = await Find(Carol, 1);
        Assert.Equal("CHOG #1001", owned?.Name);
        Assert.Equal(Carol.Value, await StoredOwner(1));
    }

    [PostgresFact]
    public async Task Single_token_check_of_an_unknown_token_changes_nothing()
    {
        Assert.Null(await Find(Alice, 7));

        Assert.Empty(await Rows());
        Assert.Empty(metadata.Calls);
    }

    [PostgresTheory]
    [InlineData(NftMetadataStatus.Invalid)]
    [InlineData(NftMetadataStatus.Unavailable)]
    public async Task Bad_or_unreachable_metadata_does_not_fail_the_request(NftMetadataStatus status)
    {
        chain.Holdings[Alice] = [1, 2];
        metadata.Respond = id => id == 2 ? new NftMetadata(status, null, null, null, []) : Good(id);

        var owned = await List(Alice);

        Assert.Equal(["CHOG #1001", "Chog #2"], owned.Select(n => n.Name));
        Assert.Null(owned[1].ImageUrl);
        Assert.Empty(owned[1].Traits);
        Assert.Equal(status, (await Rows())[1].MetadataStatus);
    }

    [PostgresFact]
    public async Task A_throwing_metadata_source_does_not_fail_the_request()
    {
        chain.Holdings[Alice] = [1];
        metadata.Respond = _ => throw new InvalidOperationException("bug");

        Assert.Equal(["Chog #1"], (await List(Alice)).Select(n => n.Name));
        Assert.Equal(NftMetadataStatus.Unavailable, (await Rows())[0].MetadataStatus);
    }

    [PostgresFact]
    public async Task Unreachable_metadata_is_retried_after_the_retry_interval_and_invalid_is_not()
    {
        chain.Holdings[Alice] = [1, 2];
        metadata.Respond = id => id == 1 ? NftMetadata.Unavailable() : NftMetadata.Invalid();
        await List(Alice);

        clock.Advance(cache.MetadataRetryInterval);
        metadata.Respond = Good;
        var owned = await List(Alice);

        Assert.Equal(["CHOG #1001", "Chog #2"], owned.Select(n => n.Name));
        Assert.Equal(3, metadata.Calls.Count);
    }

    [PostgresFact]
    public async Task Metadata_beyond_the_time_budget_is_left_for_a_later_request()
    {
        cache.MetadataFetchBudget = TimeSpan.FromMilliseconds(300);
        chain.Holdings[Alice] = [1, 2];
        metadata.Respond = Good;
        metadata.Hang = id => id == 2;

        var owned = await List(Alice);

        Assert.Equal(["CHOG #1001", "Chog #2"], owned.Select(n => n.Name));
        Assert.Equal(NftMetadataStatus.Pending, (await Rows())[1].MetadataStatus);

        metadata.Hang = _ => false;
        Assert.Equal(["CHOG #1001", "CHOG #1002"], (await List(Alice)).Select(n => n.Name));
    }

    [PostgresFact]
    public async Task During_an_outage_a_recent_scan_is_listed()
    {
        chain.Holdings[Alice] = [1, 2];
        chain.Holdings[Bob] = [];
        await List(Alice);
        await List(Bob);
        chain.Down = true;

        clock.Advance(cache.OwnershipFallback);
        Assert.Equal(["CHOG #1001", "CHOG #1002"], (await List(Alice)).Select(n => n.Name));
        Assert.Empty(await List(Bob));

        clock.Advance(TimeSpan.FromSeconds(1));
        await Assert.ThrowsAsync<NftProviderUnavailableException>(() => List(Alice));
        await Assert.ThrowsAsync<NftProviderUnavailableException>(() => List(Bob));
    }

    [PostgresFact]
    public async Task During_an_outage_a_wallet_never_scanned_is_not_reported_as_owning_nothing()
    {
        chain.Down = true;

        await Assert.ThrowsAsync<NftProviderUnavailableException>(() => List(Carol));
    }

    [PostgresFact]
    public async Task During_an_outage_single_token_checks_never_use_stored_ownership()
    {
        chain.Holdings[Alice] = [1];
        await List(Alice);
        chain.Down = true;

        await Assert.ThrowsAsync<NftProviderUnavailableException>(() => Find(Alice, 1));
        Assert.Equal(Alice.Value, await StoredOwner(1));
    }

    [PostgresFact]
    public async Task Concurrent_listings_store_each_token_and_its_traits_once()
    {
        chain.Holdings[Alice] = [1, 2, 3, 4, 5];
        metadata.Respond = Good;
        metadata.Delay = TimeSpan.FromMilliseconds(20);

        var listings = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => List(Alice))));

        Assert.All(listings, owned => Assert.Equal(5, owned.Count));
        var rows = await Rows();
        Assert.Equal(5, rows.Count);
        Assert.All(rows, row => Assert.Equal(2, row.Traits.Count));
        Assert.All(rows, row => Assert.Equal(Alice.Value, row.OwnerAddress));
    }

    [PostgresFact]
    public async Task Rows_of_another_collection_are_never_touched()
    {
        chain.Holdings[Alice] = [1];
        await List(Alice);
        await using (var db = database.CreateDbContext())
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "ChogNfts" ("ChainId", "ContractAddress", "TokenId", "OwnerAddress", "OwnerRefreshedAt", "MetadataStatus")
                VALUES ({chainId}, '0x00000000000000000000000000000000000000aa', 2, {Alice.Value}, {clock.Now}, 'Pending')
                """);
        }

        chain.Holdings[Alice] = [];
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Empty(await List(Alice));

        await using var check = database.CreateDbContext();
        var other = await check.ChogNfts.AsNoTracking().SingleAsync(n => n.ChainId == chainId && n.ContractAddress != Contract);
        Assert.Equal(Alice.Value, other.OwnerAddress);
    }

    /// <summary>Live ownership answers, as the Monad provider would give them.</summary>
    private sealed class FakeChain : INftProvider
    {
        public ConcurrentDictionary<WalletAddress, ulong[]> Holdings { get; } = new();

        public bool Down { get; set; }

        public Task<IReadOnlyList<OwnedNft>> GetOwnedAsync(WalletAddress owner, CancellationToken cancellationToken)
        {
            if (Down)
            {
                throw new NftProviderUnavailableException("down");
            }

            IReadOnlyList<OwnedNft> owned = Holdings.GetValueOrDefault(owner, [])
                .Order()
                .Select(id => new OwnedNft(NftTokenId.From(id), $"Chog #{id}", null, []))
                .ToList();
            return Task.FromResult(owned);
        }

        public Task<OwnedNft?> FindOwnedAsync(WalletAddress owner, NftTokenId tokenId, CancellationToken cancellationToken)
        {
            if (Down)
            {
                throw new NftProviderUnavailableException("down");
            }

            var owns = Holdings.GetValueOrDefault(owner, []).Any(id => id == tokenId.Value);
            return Task.FromResult(owns ? new OwnedNft(tokenId, $"Chog #{tokenId}", null, []) : null);
        }
    }

    private sealed class FakeMetadata : INftMetadataSource
    {
        public Func<BigInteger, NftMetadata> Respond { get; set; } = Good;

        public Func<BigInteger, bool> Hang { get; set; } = _ => false;

        public TimeSpan Delay { get; set; }

        public ConcurrentBag<BigInteger> Calls { get; } = [];

        public async Task<NftMetadata> FetchAsync(NftTokenId tokenId, CancellationToken cancellationToken)
        {
            Calls.Add(tokenId.Value);
            if (Hang(tokenId.Value))
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            if (Delay > TimeSpan.Zero)
            {
                await Task.Delay(Delay, cancellationToken);
            }

            return Respond(tokenId.Value);
        }
    }
}
