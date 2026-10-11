using ChogGames.Core.Nfts;
using ChogGames.Core.Players;
using ChogGames.Core.Wallets;
using ChogGames.Infrastructure.Characters;
using Microsoft.Extensions.Logging.Abstractions;

namespace ChogGames.Infrastructure.Tests.Characters;

public class CharacterEligibilityServiceTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);

    private static readonly OwnedNft Chog7 = new(
        NftTokenId.From(7), "Chog #7", new Uri("https://gateway.example.test/ipfs/bafy/7.webp"), [new("Eyes", "Laser"), new("Head", "Crown")]);

    private readonly FakeNftProvider _chain = new();
    private readonly Clock _clock = new();
    private readonly CharacterEligibilityService _service;

    public CharacterEligibilityServiceTests() =>
        _service = new CharacterEligibilityService(_chain, _clock, NullLogger<CharacterEligibilityService>.Instance);

    private static WalletAddress Wallet(char digit = 'a')
    {
        Assert.True(WalletAddress.TryParse("0x" + new string(digit, 40), out var wallet));
        return wallet;
    }

    private static Player Name(Func<PlayerDisplayName, Player> create)
    {
        Assert.True(PlayerDisplayName.TryCreate("Alice", out var name));
        return create(name);
    }

    private Player HolderOf7()
    {
        var player = Name(name => Player.CreateForWallet(Wallet(), name, Start));
        _chain.Holdings[Wallet()] = [Chog7];
        player.SelectNftCharacter(Chog7.TokenId);
        return player;
    }

    private Task<CharacterEligibility> Check(Player player) => _service.SnapshotForMatchAsync(player, CancellationToken.None);

    [Fact]
    public async Task Standard_chog_is_eligible_without_reading_the_chain()
    {
        var guest = Name(name => Player.CreateGuest(name, Start));
        Assert.True(DefaultAvatar.TryFind("gold", out var gold));
        guest.SelectStandardCharacter(gold);
        _chain.Down = true;

        var result = await Check(guest);

        Assert.Equal(CharacterEligibilityStatus.Eligible, result.Status);
        Assert.Equal(CharacterSnapshot.Standard(gold, Start), result.Snapshot);
        Assert.Equal(0, _chain.LiveChecks);
    }

    [Fact]
    public async Task Owned_nft_is_verified_live_and_snapshotted_with_its_metadata()
    {
        var player = HolderOf7();

        var result = await Check(player);

        Assert.Equal(CharacterEligibilityStatus.Eligible, result.Status);
        Assert.Equal(CharacterSnapshot.Nft(Wallet(), Chog7, Start), result.Snapshot);
        Assert.Equal(1, _chain.LiveChecks);
    }

    [Fact]
    public async Task Unowned_nft_is_rejected_and_the_selection_is_left_alone()
    {
        var player = HolderOf7();
        _chain.Holdings[Wallet()] = [];

        var result = await Check(player);

        Assert.Equal((CharacterEligibilityStatus.NftNotOwned, null), (result.Status, result.Snapshot));
        Assert.Equal((CharacterKind.Nft, "7"), (player.SelectedCharacterKind, player.SelectedCharacterId));
    }

    [Fact]
    public async Task Nft_held_by_another_wallet_is_rejected()
    {
        var player = HolderOf7();
        _chain.Holdings[Wallet()] = [];
        _chain.Holdings[Wallet('b')] = [Chog7];

        Assert.Equal(CharacterEligibilityStatus.NftNotOwned, (await Check(player)).Status);
    }

    [Fact]
    public async Task During_an_outage_an_nft_cannot_be_used_for_a_match()
    {
        var player = HolderOf7();
        _chain.Down = true;

        var result = await Check(player);

        Assert.Equal((CharacterEligibilityStatus.OwnershipUnavailable, null), (result.Status, result.Snapshot));
    }

    [Fact]
    public async Task A_transfer_or_new_choice_during_the_match_does_not_change_its_snapshot()
    {
        var player = HolderOf7();
        var snapshot = (await Check(player)).Snapshot!;

        _chain.Holdings[Wallet()] = [];
        _chain.Holdings[Wallet('b')] = [Chog7 with { Name = "Renamed", Traits = [] }];
        player.SelectStandardCharacter(DefaultAvatar.Classic);
        _clock.Advance(TimeSpan.FromMinutes(30));

        Assert.Equal(CharacterSnapshot.Nft(Wallet(), Chog7, Start), snapshot);
    }

    [Fact]
    public async Task The_next_match_checks_ownership_again()
    {
        var player = HolderOf7();
        Assert.Equal(CharacterEligibilityStatus.Eligible, (await Check(player)).Status);

        _chain.Holdings[Wallet()] = [];
        _clock.Advance(TimeSpan.FromMinutes(30));

        Assert.Equal(CharacterEligibilityStatus.NftNotOwned, (await Check(player)).Status);
        Assert.Equal(2, _chain.LiveChecks);
    }

    [Fact]
    public async Task Each_match_gets_a_fresh_snapshot_with_current_metadata()
    {
        var player = HolderOf7();
        var first = (await Check(player)).Snapshot!;

        _chain.Holdings[Wallet()] = [Chog7 with { Name = "CHOG #1462" }];
        _clock.Advance(TimeSpan.FromMinutes(30));
        var second = (await Check(player)).Snapshot!;

        Assert.Equal(("Chog #7", Start), (first.Name, first.VerifiedAt));
        Assert.Equal(("CHOG #1462", Start.AddMinutes(30)), (second.Name, second.VerifiedAt));
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = Start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    /// <summary>Chain stand-in: listing is not supported, so any use of it fails the test.</summary>
    private sealed class FakeNftProvider : INftProvider
    {
        public Dictionary<WalletAddress, List<OwnedNft>> Holdings { get; } = [];

        public bool Down { get; set; }

        public int LiveChecks { get; private set; }

        public Task<IReadOnlyList<OwnedNft>> GetOwnedAsync(WalletAddress owner, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Match eligibility must use a live single-token check, not a listing.");

        public Task<OwnedNft?> FindOwnedAsync(WalletAddress owner, NftTokenId tokenId, CancellationToken cancellationToken)
        {
            LiveChecks++;
            if (Down)
            {
                throw new NftProviderUnavailableException("Chain is down.");
            }

            return Task.FromResult(Holdings.GetValueOrDefault(owner)?.FirstOrDefault(n => n.TokenId == tokenId));
        }
    }
}
