namespace ChogGames.Api.Legal;

/// <summary>What the terms and privacy pages show that differs per deployment (<c>Legal</c>, STORY-069, DEC-070).</summary>
public sealed class LegalOptions
{
    public const string SectionName = "Legal";

    public const int MaxContactEmailLength = 254;

    /// <summary>Where players send questions and deletion requests; empty only outside production.</summary>
    public string ContactEmail { get; set; } = string.Empty;

    public bool IsValid() =>
        ContactEmail.Length == 0
        || (ContactEmail.Length <= MaxContactEmailLength
            && ContactEmail.IndexOf('@') is > 0 and var at
            && at < ContactEmail.Length - 1
            && ContactEmail.IndexOf('@', at + 1) < 0
            && !ContactEmail.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c is '<' or '>' or '"'));
}
