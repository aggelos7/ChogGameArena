using System.Text.Json;
using ChogGames.Core.Rooms;

namespace ChogGames.Core.Matches;

/// <summary>
/// A game's command resolution (ENG-012): the engine checks the envelope, the player and the version, then asks the
/// rules of the match's game what the command does (STORY-024, DEC-026). Rules are deterministic over their inputs and
/// must not keep state of their own; the match state they return is stored with the room. The engine owns the turn
/// (STORY-025, DEC-027): only the current player's commands reach the rules, the rules say which accepted command ends
/// the turn, and they give the safe default for a turn that runs out. Players only see the state through the rules'
/// per-player view (STORY-026, DEC-028). An accepted command may finish the match with the players' standings
/// (STORY-028, DEC-030). Chance comes only from the context's <see cref="IRandomSource"/> (STORY-029, ENG-008, DEC-031):
/// rules draw every die, card, event and target from it and never take an outcome from the payload, which carries only
/// the player's choices.
/// </summary>
public interface IMatchRules
{
    /// <summary>The <see cref="GameRoom.GameType"/> these rules play.</summary>
    string GameType { get; }

    /// <summary>
    /// The game's state as the match starts (STORY-036, DEC-038), such as every player's pawn on the start tile, stored
    /// with the start in its transaction. The match is seated and its first turn has begun. Null, the default, leaves
    /// the state empty until the first accepted command.
    /// </summary>
    JsonElement? StartState(MatchStartContext context) => null;

    /// <summary>
    /// The longest a match of this game may last from its start (STORY-041, ENG-011), at most
    /// <see cref="GameRoom.MaxMatchDuration"/>; null, the default, sets no limit. A match whose time is up is ended by the
    /// engine with <see cref="EndMatch"/>, so rules with a limit must implement it.
    /// </summary>
    TimeSpan? TimeLimit => null;

    /// <summary>
    /// Ends the active match now (STORY-041): its time limit was reached, or every player still playing has finished
    /// (<see cref="MatchRuleDecision.Finished"/>) and nobody is left to take a turn. Returns the final state and a rank for
    /// every player still playing, as <see cref="MatchRuleDecision.Finish"/> does. Null, the default, means these rules
    /// can't end a match by themselves; then they must set no <see cref="TimeLimit"/> and never mark a player finished.
    /// </summary>
    MatchEnding? EndMatch(MatchEndContext context) => null;

    MatchRuleDecision Apply(MatchCommandContext context);

    /// <summary>
    /// The current player's turn ran out (ENG-007): returns the game state after the game's safe default for the rest of
    /// the turn, such as resolving a pending choice deterministically, or null to skip the turn with the state unchanged.
    /// The engine then ends the turn either way.
    /// </summary>
    JsonElement? TimeOutTurn(MatchTurnContext context);

    /// <summary>
    /// What one player may see of the game state in their snapshot (STORY-026, ENG-013, DEC-028): hidden hands, upcoming
    /// cards and other players' private choices must be left out. Returns null to show the player nothing of it.
    /// </summary>
    JsonElement? ViewFor(MatchViewContext context);
}

/// <param name="Match">The match, read only, in any status; it may have ended.</param>
/// <param name="PlayerId">The player the view is for: a member, possibly one who left the match.</param>
/// <param name="State">The game's state; views are only asked for once there is one.</param>
public sealed record MatchViewContext(GameRoom Match, Guid PlayerId, JsonElement State);

/// <param name="Match">The match just started, read only: active, with every member seated.</param>
/// <param name="Random">The server's source of chance, for a start that needs a draw.</param>
public sealed record MatchStartContext(GameRoom Match, IRandomSource Random);

/// <param name="Match">The active match, read only, whose time is up or whose players have all finished.</param>
/// <param name="State">The game's state, or null while it has none.</param>
/// <param name="TimeUp">Whether the match reached its time limit; otherwise every player still playing has finished.</param>
public sealed record MatchEndContext(GameRoom Match, JsonElement? State, bool TimeUp);

/// <param name="State">The game's final state, a JSON object.</param>
/// <param name="Standings">A rank for every player still playing, as for <see cref="MatchRuleDecision.Finish"/>.</param>
public sealed record MatchEnding(JsonElement State, IReadOnlyList<MatchStanding> Standings);

/// <param name="Match">The active match, read only; <see cref="GameRoom.CurrentPlayerId"/> is the player whose turn ran out.</param>
/// <param name="State">The game's state, or null while it has none: no start state and no accepted command yet.</param>
/// <param name="Random">The server's source of chance, for a safe default that needs a draw.</param>
public sealed record MatchTurnContext(GameRoom Match, JsonElement? State, IRandomSource Random);

/// <param name="Match">The active match, read only; its members are the players.</param>
/// <param name="PlayerId">The session's player, whose turn it is.</param>
/// <param name="Payload">The player's choices only; anything in it that claims an outcome, such as a roll, is ignored.</param>
/// <param name="State">The game's state, or null while it has none: no start state and no accepted command yet.</param>
/// <param name="Random">The server's source of chance: the only place rolls, draws and random targets come from.</param>
public sealed record MatchCommandContext(
    GameRoom Match, Guid PlayerId, string Type, JsonElement Payload, JsonElement? State, IRandomSource Random);

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
/// <param name="Standings">
/// For <c>Accepted</c>: the command finished the match (STORY-028, DEC-030), with a rank for every player still playing;
/// the engine completes it, ranks those who left after them and records the results once.
/// </param>
/// <param name="Finished">
/// For <c>Accepted</c>: the acting player has finished the game, such as reaching the last tile (STORY-041); they stay
/// in the match but get no more turns. When nobody still playing is left to take a turn the engine ends the match with
/// <see cref="IMatchRules.EndMatch"/>.
/// </param>
public sealed record MatchRuleDecision(
    MatchRuleOutcome Outcome,
    JsonElement? State = null,
    string? Reason = null,
    bool EndsTurn = false,
    IReadOnlyList<MatchStanding>? Standings = null,
    bool Finished = false)
{
    public static MatchRuleDecision Accept(JsonElement state, bool endsTurn = false, bool finished = false) =>
        new(MatchRuleOutcome.Accepted, state, EndsTurn: endsTurn || finished, Finished: finished);

    /// <summary>The command ended the match with <paramref name="standings"/> for the players still playing.</summary>
    public static MatchRuleDecision Finish(JsonElement state, IReadOnlyList<MatchStanding> standings) =>
        new(MatchRuleOutcome.Accepted, state, Standings: standings);

    public static MatchRuleDecision Reject(string reason) => new(MatchRuleOutcome.Rejected, Reason: reason);

    public static readonly MatchRuleDecision Unknown = new(MatchRuleOutcome.UnknownCommand);
}
