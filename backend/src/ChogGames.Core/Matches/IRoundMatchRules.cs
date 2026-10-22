using System.Text.Json;
using ChogGames.Core.Rooms;

namespace ChogGames.Core.Matches;

/// <summary>
/// Rules of a game played in simultaneous rounds instead of turns (STORY-070, ENG-007, DEC-081). Every player still
/// playing acts in the same round: once it opens they submit privately, as often as they like, until they lock their
/// submission or its deadline passes. Each accepted command is a submission (<see cref="MatchRuleDecision.Submit"/>), kept
/// by the rules in the match state and hidden from the others by <see cref="IMatchRules.ViewFor"/>; the room shows only who
/// has submitted and locked. The engine resolves the round exactly once, at its deadline or as soon as every player still
/// playing has locked, through <see cref="ResolveRound"/>, which applies the game's default for anyone who didn't submit.
/// <see cref="IMatchRules.TimeOutTurn"/> and <see cref="IMatchRules.TimeOutReply"/> are never called for these rules.
/// </summary>
public interface IRoundMatchRules : IMatchRules
{
    /// <summary>How the match's rounds are timed, fixed at its start.</summary>
    MatchRoundTiming RoundTiming { get; }

    /// <summary>
    /// Resolves the round being played (<see cref="GameRoom.TurnNumber"/>) as one operation: the context's match tells who
    /// submitted and who locked. Returns the state the next round starts from, or, with
    /// <see cref="MatchRoundResolution.Standings"/>, the final state of a match the round finished.
    /// </summary>
    MatchRoundResolution ResolveRound(MatchRoundContext context);
}

/// <summary>
/// A round mode's timing (DEC-081): the first round opens <paramref name="FirstOpensAfter"/> after the start (such as an
/// event announcement), each later one <paramref name="OpensAfter"/> after the previous round resolved (its reveal and the
/// next announcement), and each stays open for <paramref name="Decision"/>, the round's own deadline. Commands sent before
/// a round opens are refused, so players don't lose decision time to the pauses.
/// </summary>
public sealed record MatchRoundTiming(TimeSpan Decision, TimeSpan FirstOpensAfter, TimeSpan OpensAfter)
{
    /// <summary>Throws unless the decision window lasts some time and neither pause is negative.</summary>
    public void Check()
    {
        if (Decision <= TimeSpan.Zero || FirstOpensAfter < TimeSpan.Zero || OpensAfter < TimeSpan.Zero)
        {
            throw new ArgumentException("A round's decision window lasts some time and its pauses aren't negative.");
        }
    }
}

/// <param name="Match">The active match, read only, whose round is due: its deadline passed or every player locked.</param>
/// <param name="State">The game's state, or null while it has none.</param>
/// <param name="Random">The server's source of chance, for anything the resolution draws.</param>
public sealed record MatchRoundContext(GameRoom Match, JsonElement? State, IRandomSource Random);

/// <param name="State">The game's state after the round, a JSON object.</param>
/// <param name="Standings">
/// When the round finished the match: a rank for every player still playing, as for <see cref="MatchRuleDecision.Finish"/>.
/// </param>
public sealed record MatchRoundResolution(JsonElement State, IReadOnlyList<MatchStanding>? Standings = null);
