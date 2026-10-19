namespace ChogGames.Core.Players;

/// <summary>
/// The terms of use players accept before uploading a photo (STORY-069, DEC-070). The text lives in the web app; this
/// version changes whenever it does, so players accept again before their next upload.
/// </summary>
public static class TermsOfUse
{
    /// <summary>The current terms' version: the date they were last changed.</summary>
    public const string CurrentVersion = "2026-10-19";

    public const int MaxVersionLength = 16;
}
