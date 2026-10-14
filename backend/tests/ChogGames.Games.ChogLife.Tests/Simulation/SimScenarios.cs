using ChogGames.Games.ChogLife.Abilities;

namespace ChogGames.Games.ChogLife.Tests.Simulation;

/// <summary>The kinds of tables the simulations play (STORY-044), each drawn from its seed so it replays exactly.</summary>
public static class SimScenarios
{
    public static readonly LaneStrategy[] Strategies = [LaneStrategy.Safe, LaneStrategy.Degen, LaneStrategy.Coin];

    /// <summary>Chog Life's four abilities and no ability at all (a match from before STORY-042, or a test without one).</summary>
    public static readonly string?[] Abilities =
        [LifeAbilities.RewardBoost, LifeAbilities.LossProtection, LifeAbilities.EventPreview, LifeAbilities.EventReroll, null];

    /// <summary>Everyone plays every turn, each with a lane strategy and, when <paramref name="abilities"/>, an ability.</summary>
    public static SimPlayer[] Engaged(int seed, int count, bool abilities = true)
    {
        var table = new Random(seed);
        return Enumerable.Range(0, count)
            .Select(_ => new SimPlayer(Strategies[table.Next(Strategies.Length)], abilities ? Abilities[table.Next(Abilities.Length)] : null))
            .ToArray();
    }

    /// <summary>
    /// A messy table: each player lets up to 40% of their turns run out, a third of them drop their connection for up
    /// to three minutes and come back, and one in ten leaves the match.
    /// </summary>
    public static SimPlayer[] Messy(int seed, int count)
    {
        var table = new Random(seed);
        return Engaged(seed, count).Select(player =>
        {
            var offlineFrom = TimeSpan.FromSeconds(table.Next(0, 300));
            return player with
            {
                AfkChance = table.NextDouble() * 0.4,
                Offline = table.Next(3) == 0 ? (offlineFrom, offlineFrom + TimeSpan.FromSeconds(table.Next(10, 180))) : null,
                LeavesAt = table.Next(10) == 0 ? TimeSpan.FromSeconds(table.Next(0, 600)) : null,
            };
        }).ToArray();
    }

    /// <summary>Plays <paramref name="matches"/> seeded matches from <paramref name="firstSeed"/> in parallel, in seed order.</summary>
    public static SimMatch[] PlayMany(
        LifeSimulator simulator, int matches, Func<int, SimPlayer[]> table, SimPace? pace = null, int firstSeed = 1) =>
        Enumerable.Range(firstSeed, matches)
            .AsParallel()
            .AsOrdered()
            .Select(seed => simulator.Play(seed, table(seed), pace))
            .ToArray();
}
