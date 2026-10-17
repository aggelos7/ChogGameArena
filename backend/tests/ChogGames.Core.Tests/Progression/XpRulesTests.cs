using ChogGames.Core.Progression;

namespace ChogGames.Core.Tests.Progression;

/// <summary>XP rules and seasons (STORY-059, DEC-064).</summary>
public sealed class XpRulesTests
{
    [Theory]
    [InlineData(0, 10)]
    [InlineData(1, 15)]
    [InlineData(3, 25)]
    [InlineData(5, 35)]
    public void Version_1_gives_10_per_match_and_5_per_opponent_beaten(int opponentsBeaten, int xp)
    {
        Assert.Equal((1, xp), (XpRules.V1.Version, XpRules.V1.For(opponentsBeaten)));
    }

    [Fact]
    public void Other_rules_use_their_own_values()
    {
        Assert.Equal(26, new XpRules(2, 20, 3).For(2));
    }

    [Theory]
    [InlineData("2026-10-17T12:30:00Z", "2026-10-01T00:00:00Z", "2026-11-01T00:00:00Z")]
    [InlineData("2026-10-01T00:00:00Z", "2026-10-01T00:00:00Z", "2026-11-01T00:00:00Z")]
    [InlineData("2026-10-31T23:59:59.999Z", "2026-10-01T00:00:00Z", "2026-11-01T00:00:00Z")]
    [InlineData("2026-12-31T23:00:00Z", "2026-12-01T00:00:00Z", "2027-01-01T00:00:00Z")]
    [InlineData("2026-11-01T01:00:00+03:00", "2026-10-01T00:00:00Z", "2026-11-01T00:00:00Z")] // still October in UTC
    public void A_season_is_the_UTC_calendar_month(string moment, string startsAt, string endsAt)
    {
        var season = Season.Containing(DateTimeOffset.Parse(moment));

        Assert.Equal((DateTimeOffset.Parse(startsAt), DateTimeOffset.Parse(endsAt)), (season.StartsAt, season.EndsAt));
        Assert.Equal(TimeSpan.Zero, season.StartsAt.Offset);
    }
}
