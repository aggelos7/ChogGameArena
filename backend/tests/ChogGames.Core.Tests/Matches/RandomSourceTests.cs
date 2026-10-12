using ChogGames.Core.Matches;

namespace ChogGames.Core.Tests.Matches;

/// <summary>Shuffling with the server's source of chance (STORY-029, ENG-008).</summary>
public class RandomSourceTests
{
    [Fact]
    public void Shuffle_draws_one_swap_per_position_from_the_end()
    {
        var items = new List<string> { "a", "b", "c", "d" };
        var random = new ScriptedRandomSource(0, 2, 0);

        random.Shuffle(items);

        // i=3 swaps with 0: d b c a; i=2 stays; i=1 swaps with 0: b d c a.
        Assert.Equal([(0, 4), (0, 3), (0, 2)], random.Draws);
        Assert.Equal(["b", "d", "c", "a"], items);
    }

    [Fact]
    public void Shuffle_keeps_the_order_when_every_draw_is_the_highest()
    {
        var items = new List<int> { 1, 2, 3, 4, 5 };

        ScriptedRandomSource.KeepOrder.Shuffle(items);

        Assert.Equal([1, 2, 3, 4, 5], items);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Shuffling_fewer_than_two_items_draws_nothing(int count)
    {
        var items = Enumerable.Range(0, count).ToList();
        var random = new ScriptedRandomSource();

        random.Shuffle(items);

        Assert.Empty(random.Draws);
    }

    [Fact]
    public void Every_order_of_three_items_can_be_drawn()
    {
        var orders = new HashSet<string>();
        for (var first = 0; first < 3; first++)
        {
            for (var second = 0; second < 2; second++)
            {
                var items = new List<char> { 'x', 'y', 'z' };
                new ScriptedRandomSource(first, second).Shuffle(items);
                orders.Add(new string([.. items]));
            }
        }

        Assert.Equal(6, orders.Count);
    }
}
