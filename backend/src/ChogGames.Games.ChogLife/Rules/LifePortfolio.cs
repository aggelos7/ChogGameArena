namespace ChogGames.Games.ChogLife.Rules;

/// <summary>
/// A player's economy (LIFE-006, LIFE-011, STORY-040, DEC-042): liquid MON, the value of their assets (MON staked on a
/// lane, at what was staked), bonuses and liabilities, each 0 to <see cref="LifeRules.MaxMon"/>. The
/// <see cref="Total"/> is <c>liquid + assets + bonuses − liabilities</c> and may be negative.
/// </summary>
public sealed record LifePortfolio(int Liquid, int Assets = 0, int Bonuses = 0, int Liabilities = 0)
{
    public int Total => Liquid + Assets + Bonuses - Liabilities;

    /// <summary>Whether every part is within 0 to <see cref="LifeRules.MaxMon"/>.</summary>
    public bool IsValid => new[] { Liquid, Assets, Bonuses, Liabilities }.All(part => part is >= 0 and <= LifeRules.MaxMon);

    /// <summary>
    /// The portfolio after a signed <paramref name="change"/> of MON (DEC-042). A gain repays liabilities first and the
    /// rest becomes liquid MON. A loss takes liquid MON, and what it can't take becomes a liability. Liquid MON and
    /// liabilities stay at most <see cref="LifeRules.MaxMon"/>; anything beyond is lost, or written off.
    /// </summary>
    public LifePortfolio Settle(int change)
    {
        if (change >= 0)
        {
            var repaid = Math.Min(change, Liabilities);
            return this with
            {
                Liabilities = Liabilities - repaid,
                Liquid = (int)Math.Min((long)Liquid + change - repaid, LifeRules.MaxMon),
            };
        }

        var loss = -(long)change;
        var taken = Math.Min(loss, Liquid);
        return this with
        {
            Liquid = Liquid - (int)taken,
            Liabilities = (int)Math.Min(Liabilities + loss - taken, LifeRules.MaxMon),
        };
    }
}
