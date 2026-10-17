using System.Text.RegularExpressions;
using ChogGames.Core.Achievements;

namespace ChogGames.Core.Tests.Achievements;

/// <summary>Achievement catalog v1 and progress (STORY-058, DEC-063).</summary>
public sealed class AchievementCatalogTests
{
    [Fact]
    public void Catalog_v1_has_seven_achievements_with_unique_stored_ids()
    {
        Assert.Equal(
            [
                ("first_match", AchievementGoal.GamesPlayed, 1), ("regular", AchievementGoal.GamesPlayed, 10),
                ("veteran", AchievementGoal.GamesPlayed, 50), ("first_win", AchievementGoal.Wins, 1),
                ("champion", AchievementGoal.Wins, 10), ("explorer", AchievementGoal.GamesTried, 2),
                ("full_degen", AchievementGoal.DegenScore, 100),
            ],
            Achievement.All.Select(a => (a.Id, a.Goal, a.Target)));
        Assert.Equal(Achievement.All.Count, Achievement.All.Select(a => a.Id).Distinct(StringComparer.Ordinal).Count());

        // The same rule as CK_PlayerAchievements_AchievementId_Format.
        Assert.All(Achievement.All, a => Assert.Matches(new Regex("^[a-z][a-z0-9_]{0,31}$"), a.Id));
        Assert.All(Achievement.All, a => Assert.False(string.IsNullOrWhiteSpace(a.Name) || string.IsNullOrWhiteSpace(a.Description)));
    }

    [Theory]
    [InlineData("champion", true)]
    [InlineData("Champion", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Achievements_are_found_by_their_exact_id(string? id, bool found) =>
        Assert.Equal(found, Achievement.TryFind(id, out _));

    [Fact]
    public void Each_achievement_measures_its_own_goal_and_its_progress_stops_at_the_target()
    {
        var counts = new AchievementCounts(GamesPlayed: 12, Wins: 3, GamesTried: 1, BestDegenScore: 90);
        var progress = Achievement.All.ToDictionary(a => a.Id, a => (a.ProgressFrom(counts), a.IsEarnedBy(counts)));

        Assert.Equal((1, true), progress["first_match"]);
        Assert.Equal((10, true), progress["regular"]);
        Assert.Equal((12, false), progress["veteran"]);
        Assert.Equal((1, true), progress["first_win"]);
        Assert.Equal((3, false), progress["champion"]);
        Assert.Equal((1, false), progress["explorer"]);
        Assert.Equal((90, false), progress["full_degen"]);
    }

    [Fact]
    public void No_matches_earn_nothing()
    {
        Assert.All(Achievement.All, a => Assert.Equal((0, false), (a.ProgressFrom(AchievementCounts.None), a.IsEarnedBy(AchievementCounts.None))));
        Assert.True(Achievement.All.Single(a => a.Id == "full_degen").IsEarnedBy(new AchievementCounts(0, 0, 0, 100)));
    }
}
