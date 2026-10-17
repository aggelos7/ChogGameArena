using ChogGames.Core.Collectors;
using ChogGames.Core.Players;
using ChogGames.Core.Wallets;

namespace ChogGames.Core.Tests.Collectors;

/// <summary>Collector milestones version 1 and the recorded holdings (STORY-060, DEC-065).</summary>
public sealed class CollectorTierTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);

    private static PlayerDisplayName Name(string name)
    {
        Assert.True(PlayerDisplayName.TryCreate(name, out var displayName));
        return displayName;
    }

    private static Player WalletPlayer(char digit)
    {
        Assert.True(WalletAddress.TryParse("0x" + new string(digit, 40), out var wallet));
        return Player.CreateForWallet(wallet, Name("Holder"), Now);
    }

    [Fact]
    public void Milestones_v1_are_holder_collector_patron_and_whale()
    {
        Assert.Equal(
            [("holder", "Holder", 1), ("collector", "Collector", 3), ("patron", "Patron", 5), ("whale", "Whale", 10)],
            CollectorTier.All.Select(t => (t.Id, t.Name, t.MinChogs)));
    }

    [Theory]
    [InlineData(0, null, "holder")]
    [InlineData(1, "holder", "collector")]
    [InlineData(2, "holder", "collector")]
    [InlineData(3, "collector", "patron")]
    [InlineData(5, "patron", "whale")]
    [InlineData(9, "patron", "whale")]
    [InlineData(10, "whale", null)]
    [InlineData(500, "whale", null)]
    public void The_tier_is_the_highest_milestone_reached_and_the_next_is_the_first_not_reached(int held, string? tier, string? next)
    {
        Assert.Equal((tier, next), (CollectorTier.For(held)?.Id, CollectorTier.NextAfter(held)?.Id));
    }

    [Fact]
    public void A_wallet_player_records_holdings_that_rise_and_fall()
    {
        var player = WalletPlayer('a');

        player.RecordChogsHeld(10);
        var before = player.ChogsHeld;
        player.RecordChogsHeld(2);

        Assert.Equal((10, 2), (before, player.ChogsHeld));
    }

    [Fact]
    public void Guests_hold_nothing_and_holdings_are_never_negative()
    {
        var guest = Player.CreateGuest(Name("Alice"), Now);
        var holder = WalletPlayer('b');

        Assert.Throws<InvalidOperationException>(() => guest.RecordChogsHeld(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => holder.RecordChogsHeld(-1));
        Assert.Equal((0, 0), (guest.ChogsHeld, holder.ChogsHeld));
    }
}
