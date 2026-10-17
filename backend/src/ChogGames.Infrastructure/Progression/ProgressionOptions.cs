using ChogGames.Core.Progression;

namespace ChogGames.Infrastructure.Progression;

/// <summary>XP settings (STORY-059, DEC-064), bound from the <c>Progression</c> configuration section.</summary>
public sealed class ProgressionOptions
{
    public const string SectionName = "Progression";

    public XpOptions Xp { get; set; } = new();
}

/// <summary>The XP rules for matches completed from now on; bump <see cref="Version"/> whenever the values change.</summary>
public sealed class XpOptions
{
    public int Version { get; set; } = XpRules.V1.Version;

    public int PerMatch { get; set; } = XpRules.V1.PerMatch;

    public int PerOpponentBeaten { get; set; } = XpRules.V1.PerOpponentBeaten;

    public XpRules ToRules() => new(Version, PerMatch, PerOpponentBeaten);
}
