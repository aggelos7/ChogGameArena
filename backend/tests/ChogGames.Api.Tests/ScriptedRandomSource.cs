using ChogGames.Core.Matches;

namespace ChogGames.Api.Tests;

/// <summary>
/// A scripted source of chance for deterministic tests (STORY-029, AGENTS.md rule 8). It returns the queued values in
/// order, each checked against the range asked for; once they run out it returns the highest allowed value, which leaves
/// a shuffle's order unchanged, so matches seat their players in join order unless a test scripts otherwise.
/// </summary>
public sealed class ScriptedRandomSource(params int[] values) : IRandomSource
{
    private readonly Queue<int> _values = new(values);
    private readonly List<(int Min, int Max)> _draws = [];
    private readonly object _lock = new();

    /// <summary>Never reorders a shuffle: seats stay in join order.</summary>
    public static ScriptedRandomSource KeepOrder => new();

    /// <summary>The ranges asked for so far, in order.</summary>
    public IReadOnlyList<(int Min, int Max)> Draws
    {
        get
        {
            lock (_lock)
            {
                return [.. _draws];
            }
        }
    }

    /// <summary>Queues more values to return.</summary>
    public void Enqueue(params int[] more)
    {
        lock (_lock)
        {
            foreach (var value in more)
            {
                _values.Enqueue(value);
            }
        }
    }

    public int Next(int minInclusive, int maxExclusive)
    {
        lock (_lock)
        {
            _draws.Add((minInclusive, maxExclusive));
            if (!_values.TryDequeue(out var value))
            {
                return maxExclusive - 1;
            }

            if (value < minInclusive || value >= maxExclusive)
            {
                throw new InvalidOperationException($"Scripted value {value} is outside [{minInclusive}, {maxExclusive}).");
            }

            return value;
        }
    }
}
