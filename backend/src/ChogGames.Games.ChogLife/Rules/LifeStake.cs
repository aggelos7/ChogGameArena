using ChogGames.Core.Matches;

namespace ChogGames.Games.ChogLife.Rules;

/// <summary>
/// One possible return of a lane's stake (LIFE-005, STORY-040, DEC-042): its chance in percent and the share of the
/// stake paid back, in percent (125 pays 50 MON back as 62, rounded down; 20 pays 10).
/// </summary>
public sealed record LifeStakeOutcome(int Chance, int Percent)
{
    /// <summary>What a stake of <paramref name="stake"/> MON pays back with this outcome.</summary>
    public int Payout(int stake) => (int)((long)stake * Percent / 100);
}

/// <summary>
/// What MON staked on a fork's <see cref="Lane"/> returns when the pawn rejoins the main track (LIFE-005, STORY-040,
/// DEC-042): outcomes whose chances add up to 100%. <see cref="LifeRules.Create"/> checks it.
/// </summary>
public sealed record LifeLaneStake(LifeLane Lane, IReadOnlyList<LifeStakeOutcome> Outcomes)
{
    /// <summary>
    /// The outcome chance picks: one draw of 0–99 from the server's source, or none when there is only one outcome.
    /// </summary>
    public LifeStakeOutcome Draw(IRandomSource random) => LifeChance.Pick(Outcomes, outcome => outcome.Chance, random);
}

/// <summary>
/// MON a player has staked on a lane at a fork (STORY-040, DEC-042): an asset worth what was staked until it pays out.
/// </summary>
public sealed record LifeAsset(LifeLane Lane, int Amount);

/// <summary>Picks one of several outcomes by their chances in percent, which add up to 100.</summary>
internal static class LifeChance
{
    public static T Pick<T>(IReadOnlyList<T> outcomes, Func<T, int> chance, IRandomSource random)
    {
        if (outcomes.Count == 1)
        {
            return outcomes[0];
        }

        var roll = random.Next(0, 100);
        foreach (var outcome in outcomes)
        {
            if (roll < chance(outcome))
            {
                return outcome;
            }

            roll -= chance(outcome);
        }

        return outcomes[^1];
    }
}
