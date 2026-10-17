using ChogGames.Core.Nfts;
using ChogGames.Core.Players;
using ChogGames.Core.Wallets;
using ChogGames.Infrastructure.Characters;
using ChogGames.Infrastructure.Nfts;
using Microsoft.Extensions.DependencyInjection;

namespace ChogGames.Api.Tests.Characters;

/// <summary>
/// STORY-061, DEC-066: the lobby's Chog carries stored traits only while trait-based abilities are on; under the mock
/// provider they are the fixtures'. Nothing here reads the chain or the database.
/// </summary>
public class LobbyCharactersTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);

    private static Player Holder(ulong tokenId)
    {
        Assert.True(PlayerDisplayName.TryCreate("Holder", out var name));
        var player = Player.CreateForWallet(MockNftFixtures.ManyHolder, name, Now);
        player.SelectNftCharacter(NftTokenId.From(tokenId));
        return player;
    }

    private static Player Guest()
    {
        Assert.True(PlayerDisplayName.TryCreate("Guest", out var name));
        return Player.CreateGuest(name, Now);
    }

    private static async Task<IReadOnlyDictionary<Guid, CharacterSnapshot>> LobbyAsync(string provider, bool enabled, params Player[] players)
    {
        await using var factory = new ApiFactory(ApiFactory.UnusedDatabase, new Dictionary<string, string?>
        {
            ["Nfts:Provider"] = provider,
            ["Abilities:TraitBased:Enabled"] = enabled.ToString(),
        });
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<LobbyCharacters>().ForEachAsync(players, CancellationToken.None);
    }

    [Fact]
    public async Task With_the_flag_on_a_selected_nft_carries_its_stored_traits()
    {
        var (golden, noTraits, guest) = (Holder(7), Holder(1969), Guest());

        var lobby = await LobbyAsync("Mock", enabled: true, golden, noTraits, guest);

        Assert.Equal(MockNftFixtures.Golden.Traits, lobby[golden.Id].Traits);
        Assert.Equal((CharacterKind.Nft, "7"), (lobby[golden.Id].Kind, lobby[golden.Id].CharacterId));
        Assert.Empty(lobby[noTraits.Id].Traits);
        Assert.Equal((CharacterKind.Standard, 0), (lobby[guest.Id].Kind, lobby[guest.Id].Traits.Count));
    }

    [Theory]
    [InlineData("Mock", false)]
    [InlineData("None", true)]
    public async Task Without_the_flag_or_a_trait_source_the_lobby_chog_has_no_traits(string provider, bool enabled)
    {
        var golden = Holder(7);

        var lobby = await LobbyAsync(provider, enabled, golden);

        Assert.Equal((CharacterKind.Nft, "7"), (lobby[golden.Id].Kind, lobby[golden.Id].CharacterId));
        Assert.Empty(lobby[golden.Id].Traits);
    }
}
