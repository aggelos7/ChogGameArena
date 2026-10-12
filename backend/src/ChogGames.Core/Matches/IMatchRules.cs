using System.Text.Json;
using ChogGames.Core.Rooms;

namespace ChogGames.Core.Matches;

/// <summary>
/// A game's command resolution (ENG-012): the engine checks the envelope, the player and the version, then asks the
/// rules of the match's game what the command does (STORY-024, DEC-026). Rules are deterministic over their inputs and
/// must not keep state of their own; the match state they return is stored with the room.
/// </summary>
public interface IMatchRules
{
    /// <summary>The <see cref="GameRoom.GameType"/> these rules play.</summary>
    string GameType { get; }

    MatchRuleDecision Apply(MatchCommandContext context);
}

/// <param name="Match">The active match, read only; its members are the players.</param>
/// <param name="PlayerId">The session's player, who is playing in the match.</param>
/// <param name="State">The game's state, or null before its first accepted command.</param>
public sealed record MatchCommandContext(GameRoom Match, Guid PlayerId, string Type, JsonElement Payload, JsonElement? State);

public enum MatchRuleOutcome
{
    /// <summary>The command changed the match; <see cref="MatchRuleDecision.State"/> is the new state.</summary>
    Accepted,

    /// <summary>The game knows the command but it isn't allowed now; nothing changes.</summary>
    Rejected,

    /// <summary>The game has no command of this type.</summary>
    UnknownCommand,
}

/// <param name="Reason">For <c>Rejected</c>: a stable, lower-case code clients can rely on, such as <c>not_your_turn</c>.</param>
public sealed record MatchRuleDecision(MatchRuleOutcome Outcome, JsonElement? State = null, string? Reason = null)
{
    public static MatchRuleDecision Accept(JsonElement state) => new(MatchRuleOutcome.Accepted, state);

    public static MatchRuleDecision Reject(string reason) => new(MatchRuleOutcome.Rejected, Reason: reason);

    public static readonly MatchRuleDecision Unknown = new(MatchRuleOutcome.UnknownCommand);
}
