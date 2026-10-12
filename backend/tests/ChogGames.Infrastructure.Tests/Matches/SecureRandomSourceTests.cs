using ChogGames.Core.Matches;
using ChogGames.Infrastructure.Configuration;
using ChogGames.Infrastructure.Matches;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ChogGames.Infrastructure.Tests.Matches;

/// <summary>Production randomness (STORY-029, ENG-008): secure, in range and the one the application uses.</summary>
public class SecureRandomSourceTests
{
    private readonly SecureRandomSource _random = new();

    [Fact]
    public void The_application_draws_from_the_secure_source()
    {
        var configuration = new ConfigurationBuilder().Build();
        var services = new ServiceCollection().AddInfrastructure(configuration);

        var registration = Assert.Single(services, d => d.ServiceType == typeof(IRandomSource));
        Assert.Equal((typeof(SecureRandomSource), ServiceLifetime.Singleton), (registration.ImplementationType, registration.Lifetime));
    }

    [Fact]
    public void Die_rolls_stay_in_range_and_reach_every_face()
    {
        // 6,000 rolls miss a face with a probability of about 6 × (5/6)^6000, which is effectively never.
        var rolls = Enumerable.Range(0, 6000).Select(_ => _random.Next(1, 7)).ToList();

        Assert.All(rolls, roll => Assert.InRange(roll, 1, 6));
        Assert.Equal([1, 2, 3, 4, 5, 6], rolls.Distinct().Order());
    }

    [Fact]
    public void A_single_value_range_always_returns_it()
    {
        Assert.All(Enumerable.Range(0, 20), _ => Assert.Equal(4, _random.Next(4, 5)));
    }

    [Theory]
    [InlineData(3, 3)]
    [InlineData(5, 1)]
    public void An_empty_range_is_refused(int minInclusive, int maxExclusive)
    {
        Assert.ThrowsAny<ArgumentException>(() => _random.Next(minInclusive, maxExclusive));
    }

    [Fact]
    public void Shuffles_are_not_predictable()
    {
        // Twenty shuffles of ten items all coming out the same has a probability of (1/10!)^19.
        var orders = Enumerable.Range(0, 20)
            .Select(_ =>
            {
                var items = Enumerable.Range(0, 10).ToList();
                _random.Shuffle(items);
                return string.Join(',', items);
            })
            .ToHashSet();

        Assert.True(orders.Count > 1);
    }
}
