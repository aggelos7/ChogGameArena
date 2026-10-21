using ChogGames.Core.Matches;

namespace ChogGames.Games.ChogRoyale.Rules;

/// <summary>
/// A Chog Royale round's phases (STORY-071, ROYALE-004, DEC-082): the market event is announced, players decide, the
/// server resolves and the reveal plays, then a short transition. Only the decision window takes actions; the server
/// never waits for a client's animation.
/// </summary>
public sealed record RoyaleTiming(TimeSpan Announcement, TimeSpan Decision, TimeSpan Reveal, TimeSpan Transition)
{
    /// <summary>
    /// In the shared engine (DEC-081): the first round opens after its announcement, each later one after the previous
    /// round's reveal, the transition and its own announcement.
    /// </summary>
    public MatchRoundTiming ToRounds() => new(Decision, Announcement, Reveal + Transition + Announcement);

    /// <summary>The longest one round takes, from its announcement to the end of its transition.</summary>
    public TimeSpan Round => Announcement + Decision + Reveal + Transition;
}
