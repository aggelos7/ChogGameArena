namespace ChogGames.Core.Achievements;

/// <summary>What an achievement counts (STORY-058, DEC-063).</summary>
public enum AchievementGoal
{
    /// <summary>Completed matches, as the profile's games played (forfeits included).</summary>
    GamesPlayed,

    /// <summary>Completed matches ranked first, as the profile's wins.</summary>
    Wins,

    /// <summary>Different games with at least one completed match.</summary>
    GamesTried,

    /// <summary>The best Degen Score in any game's stored result.</summary>
    DegenScore,
}

/// <summary>
/// A cosmetic achievement (STORY-058, DEC-063): unlocked once, from verified match results only, when its
/// <see cref="Goal"/> reaches <see cref="Target"/>. It never changes a match.
/// </summary>
public sealed record Achievement(string Id, string Name, string Description, AchievementGoal Goal, int Target)
{
    public const int MaxIdLength = 32;

    /// <summary>Catalog version 1, in the order the profile lists it.</summary>
    public static IReadOnlyList<Achievement> All { get; } =
    [
        new("first_match", "First Match", "Complete a match.", AchievementGoal.GamesPlayed, 1),
        new("regular", "Regular", "Complete 10 matches.", AchievementGoal.GamesPlayed, 10),
        new("veteran", "Veteran", "Complete 50 matches.", AchievementGoal.GamesPlayed, 50),
        new("first_win", "First Win", "Win a match.", AchievementGoal.Wins, 1),
        new("champion", "Champion", "Win 10 matches.", AchievementGoal.Wins, 10),
        new("explorer", "Explorer", "Complete a match in two different games.", AchievementGoal.GamesTried, 2),
        new("full_degen", "Full Degen", "Reach a Degen Score of 100 in a match.", AchievementGoal.DegenScore, 100),
    ];

    public static bool TryFind(string? id, out Achievement achievement)
    {
        achievement = All.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.Ordinal))!;
        return achievement is not null;
    }

    /// <summary>How far <paramref name="counts"/> go towards this achievement, never past its target.</summary>
    public int ProgressFrom(AchievementCounts counts) => Math.Clamp(counts.Of(Goal), 0, Target);

    public bool IsEarnedBy(AchievementCounts counts) => counts.Of(Goal) >= Target;
}

/// <summary>A player's totals over their completed matches, which every achievement is measured against.</summary>
public sealed record AchievementCounts(int GamesPlayed, int Wins, int GamesTried, int BestDegenScore)
{
    public static AchievementCounts None { get; } = new(0, 0, 0, 0);

    public int Of(AchievementGoal goal) => goal switch
    {
        AchievementGoal.GamesPlayed => GamesPlayed,
        AchievementGoal.Wins => Wins,
        AchievementGoal.GamesTried => GamesTried,
        AchievementGoal.DegenScore => BestDegenScore,
        _ => throw new ArgumentOutOfRangeException(nameof(goal), goal, "Unknown achievement goal."),
    };
}
