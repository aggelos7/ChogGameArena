namespace ChogGames.Core.Progression;

/// <summary>
/// How much XP a completed match earns (STORY-059, DEC-064): <see cref="PerMatch"/> for each player still playing, plus
/// <see cref="PerOpponentBeaten"/> for every member ranked below them. Players who left earn nothing. Every award is
/// stored with <see cref="Version"/>, so changing the values only affects later matches.
/// </summary>
public sealed record XpRules(int Version, int PerMatch, int PerOpponentBeaten)
{
    public const int MaxValue = 1000;

    /// <summary>Version 1, as the migration backfilled the matches played before it.</summary>
    public static XpRules V1 { get; } = new(1, 10, 5);

    public int For(int opponentsBeaten) => PerMatch + PerOpponentBeaten * opponentsBeaten;
}
