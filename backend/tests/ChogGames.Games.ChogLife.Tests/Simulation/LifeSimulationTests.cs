using ChogGames.Core.Games;
using ChogGames.Games.ChogLife.Rules;
using static ChogGames.Games.ChogLife.Tests.Simulation.LifeBalanceReport;

namespace ChogGames.Games.ChogLife.Tests.Simulation;

/// <summary>
/// STORY-044, DEC-047: seeded simulations of whole Chog Life matches through the real engine, rules and abilities.
/// No match gets stuck, whatever the players do: every step changes the room, refusals of legal moves fail the run
/// (<see cref="LifeSimulator"/>), and every match ends, within its 20-minute cap, by everyone finishing, the time cap or
/// forfeits. With everyone playing, every match reaches the finish within the revised duration band, and the balance
/// of rules version 2 stays where the simulations put it.
/// </summary>
public class LifeSimulationTests
{
    private static readonly LifeSimulator Simulator = new();

    private static readonly TimeSpan Cap = LifeRulesV2.Rules.TimeCap;

    /// <summary>
    /// Nobody idles, though a slow player can still run out of time on a fork turn that rolls, previews, chooses and
    /// keeps in one 25-second turn; the rules then take the default lane or keep it, and the match goes on.
    /// </summary>
    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(6)]
    public void Matches_where_everyone_plays_all_reach_the_finish_with_everyone_ranked(int count)
    {
        var matches = SimScenarios.PlayMany(Simulator, 300, seed => SimScenarios.Engaged(seed, count));

        Assert.All(matches, match =>
        {
            Assert.Equal(SimEnding.Finish, match.Ending);
            Assert.All(match.Players, p => Assert.False(p.Left));
            Assert.Equal(Enumerable.Range(1, count), match.Players.Select(p => p.Place!.Value).Order());
            Assert.Contains(match.Players, p => p.Rank == 1);
            Assert.All(match.Players, p => Assert.NotNull(p.Result));
        });
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(6)]
    public void Messy_matches_with_afk_players_reconnects_and_leavers_never_get_stuck(int count)
    {
        var matches = SimScenarios.PlayMany(Simulator, 300, seed => SimScenarios.Messy(seed, count));

        Assert.All(matches, match =>
        {
            Assert.True(match.Duration <= Cap, $"Seed {match.Seed} lasted {match.Duration}.");
            if (match.Ending == SimEnding.Abandoned)
            {
                Assert.True(match.Players.Count(p => !p.Left) < GameDefinition.MinRoomPlayers, $"Seed {match.Seed} was abandoned with players left.");
            }
            else
            {
                Assert.All(match.Players, p => Assert.Equal(p.Left, p.Result is null));
            }
        });
        Assert.Contains(matches, match => match.TimedOutTurns > 0);
        Assert.Contains(matches, match => match.Players.Any(p => p.Left));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(6)]
    public void A_table_where_nobody_plays_forfeits_turn_by_turn_instead_of_waiting(int count)
    {
        var match = Simulator.Play(1, Enumerable.Range(0, count).Select(_ => new SimPlayer(LaneStrategy.Safe, AfkChance: 1)).ToArray());

        // Three missed turns each forfeit (DEC-027): two full rounds, then one more each until one player is left.
        Assert.Equal(SimEnding.Abandoned, match.Ending);
        Assert.Equal((2 * count) + (count - 1), match.TimedOutTurns);
        Assert.Equal(TimeSpan.FromSeconds(25) * match.TimedOutTurns, match.Duration);
        Assert.Equal(count - 1, match.Players.Count(p => p.Left));
    }

    [Fact]
    public void A_player_who_reconnects_within_two_missed_turns_plays_on_to_the_finish()
    {
        var matches = Enumerable.Range(1, 50).Select(seed => Simulator.Play(seed,
        [
            new SimPlayer(LaneStrategy.Coin),
            new SimPlayer(LaneStrategy.Coin, Offline: (TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(35))),
        ])).ToArray();

        Assert.All(matches, match =>
        {
            Assert.Equal(SimEnding.Finish, match.Ending);
            Assert.InRange(match.TimedOutTurns, 1, 2);
            Assert.All(match.Players, p => Assert.False(p.Left));
            Assert.NotNull(match.Players[1].Place);
        });
    }

    [Fact]
    public void A_player_away_for_three_turns_forfeits_and_the_others_finish()
    {
        var matches = Enumerable.Range(1, 50).Select(seed => Simulator.Play(seed,
        [
            new SimPlayer(LaneStrategy.Coin),
            new SimPlayer(LaneStrategy.Coin, Offline: (TimeSpan.Zero, TimeSpan.FromMinutes(5))),
            new SimPlayer(LaneStrategy.Coin),
        ])).ToArray();

        Assert.All(matches, match =>
        {
            Assert.Equal(SimEnding.Finish, match.Ending);
            Assert.True(match.Players[1].Left);
            Assert.Null(match.Players[1].Rank);
            Assert.All([match.Players[0], match.Players[2]], p => Assert.NotNull(p.Place));
        });
    }

    [Fact]
    public void A_player_who_leaves_mid_match_is_out_and_the_others_are_ranked()
    {
        var match = Simulator.Play(7,
        [
            new SimPlayer(LaneStrategy.Safe),
            new SimPlayer(LaneStrategy.Degen),
            new SimPlayer(LaneStrategy.Coin, LeavesAt: TimeSpan.FromSeconds(30)),
            new SimPlayer(LaneStrategy.Safe),
        ]);

        Assert.Equal(SimEnding.Finish, match.Ending);
        Assert.True(match.Players[2].Left);
        Assert.Null(match.Players[2].Result);
        Assert.Equal([1, 2, 3], match.Players.Where(p => !p.Left).Select(p => p.Place!.Value).Order());
    }

    [Fact]
    public void Slow_players_on_a_full_table_reach_the_time_cap_and_are_still_ranked()
    {
        var crawl = new SimPace(TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(24), TimeSpan.FromSeconds(25));

        var match = Simulator.Play(3, SimScenarios.Engaged(3, 6), crawl);

        Assert.Equal(SimEnding.TimeUp, match.Ending);
        Assert.Equal(Cap, match.Duration);
        Assert.Contains(match.Players, p => p.Place is null);
        Assert.All(match.Players, p => Assert.NotNull(p.Rank));
    }

    [Fact]
    public void The_same_seed_replays_the_same_match()
    {
        var table = SimScenarios.Messy(42, 4);

        var first = Simulator.Play(42, table);
        var again = Simulator.Play(42, table);

        Assert.Equal((first.Ending, first.Duration, first.Turns, first.Commands), (again.Ending, again.Duration, again.Turns, again.Commands));
        Assert.Equal(first.Players.Select(p => p.Result?.GetRawText()), again.Players.Select(p => p.Result?.GetRawText()));
    }

    /// <summary>DEC-047: with players taking 2–8 seconds an action, matches last about 2, 4 and 6 minutes.</summary>
    [Theory]
    [InlineData(2, 1, 3)]
    [InlineData(4, 3, 5)]
    [InlineData(6, 5, 7.5)]
    public void Engaged_matches_last_as_documented(int count, double minMedian, double maxMedian)
    {
        var matches = SimScenarios.PlayMany(Simulator, 300, seed => SimScenarios.Engaged(seed, count));

        Assert.InRange(Percentile(matches.Select(m => m.Duration), 50).TotalMinutes, minMedian, maxMedian);
        Assert.True(matches.Max(m => m.Duration) < TimeSpan.FromMinutes(10));
    }

    /// <summary>
    /// DEC-047: reaching the Finish first helps without deciding the match, turn order gives no real edge, and neither
    /// lane nor any ability wins far more than its share. 600 seeded matches per table size; the bounds are wide enough
    /// for the sample and narrow enough to catch a rules change that tips the balance.
    /// </summary>
    [Theory]
    [InlineData(2, 55, 75)]
    [InlineData(4, 30, 50)]
    [InlineData(6, 20, 40)]
    public void Version_2_keeps_the_balance_the_simulations_found(int count, double minFirstWins, double maxFirstWins)
    {
        var matches = SimScenarios.PlayMany(Simulator, 600, seed => SimScenarios.Engaged(seed, count));
        var players = matches.SelectMany(m => m.Players).ToArray();
        var fair = 100.0 / count;

        double WinShare(Func<SimPlayerResult, bool> which)
        {
            var group = players.Where(which).ToArray();
            return Share(group.Count(p => p.Rank == 1), group.Length);
        }

        Assert.InRange(WinShare(p => p.Place == 1), minFirstWins, maxFirstWins);
        Assert.InRange(WinShare(p => p.Seat == 1), fair - 6, fair + 6);
        Assert.All(SimScenarios.Strategies, lanes => Assert.InRange(WinShare(p => p.Player.Lanes == lanes), fair - 8, fair + 8));
        Assert.All(SimScenarios.Abilities, ability => Assert.InRange(WinShare(p => p.Player.Ability == ability), fair - 8, fair + 8));
    }

    [Fact]
    public void The_simulator_plays_the_rules_new_matches_start_with()
    {
        var match = Simulator.Play(1, SimScenarios.Engaged(1, 2, abilities: false));

        var first = match.Players.Single(p => p.Place == 1);
        Assert.Equal(LifeRulesV2.FinishBonus, first.Result!.Value.GetProperty("bonuses").GetInt32());
    }
}
