using System.Text.Json;
using ChogGames.Core.Rooms;

namespace ChogGames.Core.Matches;

/// <summary>
/// A game's command resolution (ENG-012): the engine checks the envelope, the player and the version, then asks the
/// rules of the match's game what the command does (STORY-024, DEC-026). Rules are deterministic over their inputs and
/// must not keep state of their own; the match state they return is stored with the room. The engine owns the turn
/// (STORY-025, DEC-027): only the current player's commands reach the rules, the rules say which accepted command ends
/// the turn, and they give the safe default for a turn that runs out.
/// </summary>
public interface IMatchRules
{
    /// <summary>The <see cref="GameRoom.GameType"/> these rules play.</summary>
    string GameType { get; }

    MatchRuleDecision Apply(MatchCommandContext context);

    /// <summary>
    /// The current player's turn ran out (ENG-007): returns the game state after the game's safe default for the rest of
    /// the turn, such as resolving a pending choice deterministically, or null to skip the turn with the state unchanged.
    /// The engine then ends the turn either way.
    /// </summary>
    JsonElement? TimeOutTurn(MatchTurnContext context);
}

/// <param name="Match">The active match, read only; <see cref="GameRoom.CurrentPlayerId"/> is the player whose turn ran out.</param>
/// <param name="State">The game's state, or null before its first accepted command.</param>
public sealed record MatchTurnContext(GameRoom Match, JsonElement? State);

/// <param name="Match">The active match, read only; its members are the players.</param>
/// <param name="PlayerId">The session's player, whose turn it is.</param>
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

/// <param name="Reason">For <c>Rejected</c>: a stable, lower-case code clients can rely on, such as <c>must_roll_first</c>.</param>
/// <param name="EndsTurn">For <c>Accepted</c>: the command finished the player's turn, so the next player's begins.</param>
public sealed record MatchRuleDecision(MatchRuleOutcome Outcome, JsonElement? State = null, string? Reason = null, bool EndsTurn = false)
{
    public static MatchRuleDecision Accept(JsonElement state, bool endsTurn = false) => new(MatchRuleOutcome.Accepted, state, EndsTurn: endsTurn);

    public static MatchRuleDecision Reject(string reason) => new(MatchRuleOutcome.Rejected, Reason: reason);

    public static readonly MatchRuleDecision Unknown = new(MatchRuleOutcome.UnknownCommand);
}
