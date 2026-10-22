using ChogGames.Core.Rooms;

namespace ChogGames.Games.ChogRoyale.Tests.Simulation;

/// <summary>
/// STORY-079 (ROYALE-063, ROYALE-065): seeded full matches through the real command path. Matches of two and six players
/// always finish after five rounds, within the five-minute target, with balances never below zero and no legal command
/// refused; messy tables never stall; and the same seed always plays the same history and ranking. The balance report
/// (<see cref="RoyaleBalanceReportTests"/>) plays many more.
/// </summary>
public class RoyaleSimulationTests
{
    private static readonly RoyaleSimulator Simulator = new();

    [Theory]
    [InlineData(2)]
    [InlineData(6)]
    public void Matches_always_finish_after_five_rounds_with_non_negative_balances(int count)
    {
        var matches = RoyaleScenarios.PlayMany(Simulator, 300, seed => RoyaleScenarios.Engaged(seed, count));

        Assert.All(matches, match =>
        {
            Assert.Equal((RoomStatus.Completed, 5), (match.Ending, match.Rounds));
            Assert.InRange(match.Duration, TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(5));
            Assert.All(match.Ledger.SelectMany(r => r.Entries), e => Assert.True(e.Balance >= 0));
            Assert.Contains(match.Players, p => p.Rank == 1);
        });
        Assert.Contains(matches, m => m.AbilityUses > 0);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(6)]
    public void Messy_tables_never_stall(int count)
    {
        var matches = RoyaleScenarios.PlayMany(Simulator, 300, seed => RoyaleScenarios.Messy(seed, count));

        Assert.All(matches, match =>
        {
            Assert.True(match.Ending is RoomStatus.Completed or RoomStatus.Abandoned);
            Assert.True(match.Ending == RoomStatus.Abandoned || match.Rounds == 5);
            Assert.True(match.Duration <= TimeSpan.FromMinutes(5));
        });
    }

    [Fact]
    public void A_table_nobody_plays_hodls_through_five_rounds_in_its_longest_time()
    {
        var idle = Enumerable.Repeat(new RoyaleSimPlayer(RoyaleStyle.Random, AfkChance: 1), 6).ToArray();

        var match = Simulator.Play(1, idle);

        // 5 s announcement, then five 25 s windows with 20 s between them; the last reveal plays over the results: 3 min 30 s.
        Assert.Equal((RoomStatus.Completed, 5, TimeSpan.FromSeconds(210)), (match.Ending, match.Rounds, match.Duration));
        // Everyone HODLs by default; only a Community Airdrop moves MON, the same for all, so all share first place.
        Assert.All(match.Ledger.SelectMany(r => r.Entries), e => Assert.True(e.ByDefault && e.Card is null && e.Change == e.Market));
        Assert.All(match.Players, p => Assert.Equal((1, match.Players[0].Mon), (p.Rank!.Value, p.Mon)));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(6)]
    public void The_same_seeds_give_the_same_histories_and_rankings(int count)
    {
        foreach (var seed in Enumerable.Range(1, 50))
        {
            var first = Simulator.Play(seed, RoyaleScenarios.Messy(seed, count));
            var again = Simulator.Play(seed, RoyaleScenarios.Messy(seed, count));

            Assert.Equal(RoyaleSimulator.History(first), RoyaleSimulator.History(again));
            Assert.Equal(first.State, again.State);
            Assert.Equal(first.Players.Select(p => (p.Rank, p.Mon)), again.Players.Select(p => (p.Rank, p.Mon)));
        }

        Assert.NotEqual(
            RoyaleSimulator.History(Simulator.Play(1, RoyaleScenarios.Engaged(1, count))),
            RoyaleSimulator.History(Simulator.Play(2, RoyaleScenarios.Engaged(1, count))));
    }
}
