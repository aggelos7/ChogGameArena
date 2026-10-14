using ChogGames.Core.Matches;

namespace ChogGames.Games.ChogLife.Rules;

/// <summary>Whether an event outcome adds MON or takes it.</summary>
public enum LifeEffect
{
    Gain,
    Loss,
}

/// <summary>
/// One possible result of an event (LIFE-004, STORY-038, DEC-040): its chance in percent and how much liquid MON it
/// moves, a fixed <see cref="Amount"/> plus <see cref="Percent"/> of the player's liquid MON (rounded down), but at least
/// <see cref="Minimum"/>. A loss bigger than the player's liquid MON leaves the rest as a liability (LIFE-011,
/// STORY-040, DEC-042); <see cref="LifePortfolio.Settle"/> applies it.
/// </summary>
public sealed record LifeEventOutcome(int Chance, LifeEffect Effect, int Amount = 0, int Percent = 0, int Minimum = 0)
{
    /// <summary>Gains a fixed <paramref name="amount"/> of MON.</summary>
    public static LifeEventOutcome Gain(int chance, int amount) => new(chance, LifeEffect.Gain, Amount: amount);

    /// <summary>Gains <paramref name="percent"/> of the player's liquid MON, at least <paramref name="minimum"/>.</summary>
    public static LifeEventOutcome GainPercent(int chance, int percent, int minimum = 0) =>
        new(chance, LifeEffect.Gain, Percent: percent, Minimum: minimum);

    /// <summary>Loses <paramref name="percent"/> of the player's liquid MON, at least <paramref name="minimum"/>.</summary>
    public static LifeEventOutcome LosePercent(int chance, int percent, int minimum = 0) =>
        new(chance, LifeEffect.Loss, Percent: percent, Minimum: minimum);

    /// <summary>The signed MON this outcome moves for a player holding <paramref name="liquid"/> MON.</summary>
    public int Change(int liquid)
    {
        var size = Math.Max(Minimum, Amount + (int)((long)liquid * Percent / 100));
        return Effect == LifeEffect.Gain ? size : -size;
    }
}

/// <summary>
/// What an event tile of one <see cref="Kind"/> does (LIFE-004, STORY-038, DEC-040): its outcomes, whose chances add up
/// to 100%. <see cref="LifeRules.Create"/> checks it with the board.
/// </summary>
public sealed record LifeEvent(LifeEventKind Kind, IReadOnlyList<LifeEventOutcome> Outcomes)
{
    /// <summary>
    /// The outcome chance picks: one draw of 0–99 from the server's source, or none when there is only one outcome.
    /// </summary>
    public LifeEventOutcome Draw(IRandomSource random) => LifeChance.Pick(Outcomes, outcome => outcome.Chance, random);
}
