namespace ChogGames.Core.Matches;

/// <summary>
/// The server's source of chance (ADR-003, ENG-008, STORY-029): every die, card, event, target and turn order is drawn
/// from it, never taken from a client. Production uses cryptographically secure randomness, so outcomes can't be
/// predicted; tests inject a scripted source so game rules stay deterministic.
/// </summary>
public interface IRandomSource
{
    /// <summary>A uniformly chosen integer with <paramref name="minInclusive"/> ≤ result &lt; <paramref name="maxExclusive"/>.</summary>
    int Next(int minInclusive, int maxExclusive);
}

public static class RandomSourceExtensions
{
    /// <summary>Puts <paramref name="items"/> in a uniformly random order in place (Fisher–Yates).</summary>
    public static void Shuffle<T>(this IRandomSource random, IList<T> items)
    {
        for (var i = items.Count - 1; i > 0; i--)
        {
            var j = random.Next(0, i + 1);
            (items[i], items[j]) = (items[j], items[i]);
        }
    }
}
