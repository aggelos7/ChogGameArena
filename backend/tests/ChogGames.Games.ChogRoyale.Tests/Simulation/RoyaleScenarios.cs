using ChogGames.Games.ChogRoyale.Abilities;

namespace ChogGames.Games.ChogRoyale.Tests.Simulation;

/// <summary>The tables the simulations and the balance report play (STORY-079), each drawn from its seed.</summary>
public static class RoyaleScenarios
{
    public static readonly string[] Abilities =
        [RoyaleAbilities.DiamondHandsPlus, RoyaleAbilities.AlphaLeak, RoyaleAbilities.DoublePump, RoyaleAbilities.StopLoss];

    /// <summary>Engaged players of random styles, each with a random ability or, when <paramref name="abilities"/> is false, none.</summary>
    public static RoyaleSimPlayer[] Engaged(int seed, int count, bool abilities = true, RoyaleStyle? style = null)
    {
        var random = new Random(seed);
        var styles = Enum.GetValues<RoyaleStyle>();
        return
        [
            .. Enumerable.Range(0, count).Select(_ => new RoyaleSimPlayer(
                style ?? styles[random.Next(styles.Length)],
                abilities ? Abilities[random.Next(Abilities.Length)] : null)),
        ];
    }

    /// <summary>
    /// Engaged players, of whom each lets up to 40% of rounds pass, thinks for up to 30 s (past the 25 s window), and one
    /// in ten leaves before a random round.
    /// </summary>
    public static RoyaleSimPlayer[] Messy(int seed, int count)
    {
        var random = new Random(unchecked(seed + 7));
        return
        [
            .. Engaged(seed, count).Select(player => player with
            {
                AfkChance = random.NextDouble() * 0.4,
                MinThink = 1,
                MaxThink = 30,
                LeavesInRound = random.NextDouble() < 0.1 ? random.Next(1, 6) : null,
            }),
        ];
    }

    public static List<RoyaleSimMatch> PlayMany(RoyaleSimulator simulator, int matches, Func<int, RoyaleSimPlayer[]> table, int firstSeed = 1) =>
        [.. Enumerable.Range(firstSeed, matches).Select(seed => simulator.Play(seed, table(seed)))];
}
