using ChogGames.Core.Nfts;
using ChogGames.Core.Players;
using ChogGames.Core.Wallets;
using ChogGames.Infrastructure.Characters;
using ChogGames.Infrastructure.Nfts;
using Microsoft.Extensions.DependencyInjection;

namespace ChogGames.Api.Tests.Characters;

/// <summary>The registered service checks the configured provider; here the offline mock fixtures.</summary>
public class CharacterEligibilityRegistrationTests
{
    private static Player WalletPlayerSelecting(WalletAddress wallet, ulong tokenId)
    {
        Assert.True(PlayerDisplayName.TryCreate("Alice", out var name));
        var player = Player.CreateForWallet(wallet, name, DateTimeOffset.UtcNow);
        player.SelectNftCharacter(NftTokenId.From(tokenId));
        return player;
    }

    [Theory]
    [InlineData("c001", 1, CharacterEligibilityStatus.Eligible)]
    [InlineData("c002", 1, CharacterEligibilityStatus.NftNotOwned)]
    [InlineData("c000", 1, CharacterEligibilityStatus.NftNotOwned)]
    [InlineData("c003", 1, CharacterEligibilityStatus.OwnershipUnavailable)]
    public async Task Selected_nft_is_checked_against_the_configured_provider(string fixture, ulong tokenId, CharacterEligibilityStatus expected)
    {
        await using var factory = new ApiFactory(ApiFactory.UnusedDatabase, new Dictionary<string, string?> { ["Nfts:Provider"] = "Mock" });
        using var scope = factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<CharacterEligibilityService>();
        Assert.True(WalletAddress.TryParse("0x" + new string('0', 36) + fixture, out var wallet));

        var result = await service.SnapshotForMatchAsync(WalletPlayerSelecting(wallet, tokenId), CancellationToken.None);

        Assert.Equal(expected, result.Status);
        if (expected == CharacterEligibilityStatus.Eligible)
        {
            Assert.Equal((MockNftFixtures.Classic.Name, wallet.Value), (result.Snapshot!.Name, result.Snapshot.WalletAddress));
            Assert.Equal(MockNftFixtures.Classic.Traits, result.Snapshot.Traits);
        }
    }
}
