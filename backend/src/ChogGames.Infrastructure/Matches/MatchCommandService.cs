using System.Text.Json;
using ChogGames.Core.Abilities;
using ChogGames.Core.Matches;
using ChogGames.Core.Players;
using ChogGames.Core.Rooms;
using ChogGames.Infrastructure.Persistence;
using ChogGames.Infrastructure.Progression;
using ChogGames.Infrastructure.Rooms;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;

namespace ChogGames.Infrastructure.Matches;

public enum MatchCommandOutcome
{
    /// <summary>The rules accepted the command; the match changed and the action was recorded.</summary>
    Applied,

    /// <summary>The action had already been applied; its stored result is returned and nothing changed.</summary>
    Replayed,

    /// <summary>No such room, the player isn't a member, or the command names another match.</summary>
    RoomNotFound,

    /// <summary>The action id was already used in this match with another type, payload or player.</summary>
    ActionConflict,

    /// <summary>The room is still waiting.</summary>
    NotStarted,

    /// <summary>The match is over or the room was cancelled.</summary>
    RoomClosed,

    /// <summary>The player left (forfeited) this match.</summary>
    NotPlaying,

    /// <summary>The command's expected version is not the current one.</summary>
    StaleVersion,

    /// <summary>Another player is playing the current turn.</summary>
    NotYourTurn,

    /// <summary>The player's turn ran out; it is about to be timed out.</summary>
    TurnExpired,

    /// <summary>The match's time is up, or every player has finished; it is about to end (STORY-041).</summary>
    TimeUp,

    /// <summary>The match's game has no command of this type.</summary>
    UnknownCommand,

    /// <summary>The game's rules refused the command now; see <see cref="MatchCommandResult.Reason"/>.</summary>
    Rejected,
}

/// <param name="Action">For <c>Applied</c> and <c>Replayed</c>: the recorded action, with the version it produced.</param>
/// <param name="CurrentVersion">The room's version now, for <c>Applied</c>, <c>Replayed</c> and <c>StaleVersion</c>.</param>
/// <param name="Reason">For <c>Rejected</c>: the rules' stable reason code.</param>
public sealed record MatchCommandResult(
    MatchCommandOutcome Outcome, MatchAction? Action = null, long? CurrentVersion = null, string? Reason = null)
{
    public bool Succeeded => Action is not null;
}

/// <summary>
/// Runs match commands (STORY-024, ENG-009, DEC-026). Each runs in a transaction under the room's row lock, so commands
/// for one match are processed one at a time: a retried action id returns its first result, a command built on an older
/// version is refused, and of several commands built on the same version exactly one can succeed. The game's
/// <see cref="IMatchRules"/> decide what an accepted command does; the player is always the session's. Only the current
/// player acts, before their turn runs out (STORY-025, DEC-027), or the player the turn waits for while a reply window is
/// open (STORY-054, DEC-058). A command that finishes the match stores the standings
/// and updates the players' stats in the same transaction, so results are recorded exactly once (STORY-028, DEC-030).
/// The rules draw every outcome from the server's <see cref="IRandomSource"/>, never from the payload (STORY-029, DEC-031).
/// <c>USE_ABILITY</c> goes to the shared <see cref="AbilityEngine"/> instead of the rules: under the same lock and checks,
/// a use is applied and counted once, and a refused one changes nothing (STORY-034, DEC-036).
/// </summary>
public sealed class MatchCommandService(
    ChogGamesDbContext db,
    IEnumerable<IMatchRules> rules,
    AbilityEngine abilities,
    IRandomSource random,
    TimeProvider timeProvider,
    IOptions<ProgressionOptions> progression)
{
    /// <summary>Why an ability can't be used while the turn waits for another player's reply (DEC-058).</summary>
    public const string ReplyPending = "reply_pending";

    private readonly Dictionary<string, IMatchRules> _rulesByGame = rules.ToDictionary(r => r.GameType, StringComparer.Ordinal);

    public async Task<MatchCommandResult> ExecuteAsync(Player player, RoomCode code, MatchCommand command, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var room = await db.LockRoomAsync(code, cancellationToken);

        // A command for another match looks like an unknown room, as for non-members.
        if (room is null || !room.IsMember(player.Id) || command.MatchId != room.Id)
        {
            return new MatchCommandResult(MatchCommandOutcome.RoomNotFound);
        }

        // Retries are answered before any other check, so a retry still gets its result after the match moved on or ended.
        var payload = command.Payload.GetRawText();
        var processed = await db.MatchActions.AsNoTracking()
            .SingleOrDefaultAsync(a => a.RoomId == room.Id && a.ActionId == command.ActionId, cancellationToken);
        if (processed is not null)
        {
            return await IsSameActionAsync(processed, player, command.Type, payload, cancellationToken)
                ? new MatchCommandResult(MatchCommandOutcome.Replayed, processed, room.Version)
                : new MatchCommandResult(MatchCommandOutcome.ActionConflict);
        }

        var now = timeProvider.GetUtcNow();
        switch (room.CheckCommand(player.Id, command, now))
        {
            case MatchCommandProblem.WrongMatch:
                return new MatchCommandResult(MatchCommandOutcome.RoomNotFound);
            case MatchCommandProblem.NotStarted:
                return new MatchCommandResult(MatchCommandOutcome.NotStarted);
            case MatchCommandProblem.Closed:
                return new MatchCommandResult(MatchCommandOutcome.RoomClosed);
            case MatchCommandProblem.NotPlaying:
                return new MatchCommandResult(MatchCommandOutcome.NotPlaying);
            case MatchCommandProblem.StaleVersion:
                return new MatchCommandResult(MatchCommandOutcome.StaleVersion, CurrentVersion: room.Version);
            case MatchCommandProblem.NotYourTurn:
                return new MatchCommandResult(MatchCommandOutcome.NotYourTurn);
            case MatchCommandProblem.TurnExpired:
                return new MatchCommandResult(MatchCommandOutcome.TurnExpired);
            case MatchCommandProblem.TimeUp:
                return new MatchCommandResult(MatchCommandOutcome.TimeUp);
        }

        using var state = room.State is null ? null : JsonDocument.Parse(room.State);
        if (string.Equals(command.Type, MatchCommand.UseAbility, StringComparison.Ordinal))
        {
            return await UseAbilityAsync(room, player, command, state?.RootElement, now, transaction, cancellationToken);
        }

        if (!_rulesByGame.TryGetValue(room.GameType, out var gameRules))
        {
            return new MatchCommandResult(MatchCommandOutcome.UnknownCommand);
        }

        var decision = gameRules.Apply(new MatchCommandContext(room, player.Id, command.Type, command.Payload, state?.RootElement, random));
        switch (decision.Outcome)
        {
            case MatchRuleOutcome.UnknownCommand:
                return new MatchCommandResult(MatchCommandOutcome.UnknownCommand);
            case MatchRuleOutcome.Rejected:
                return new MatchCommandResult(MatchCommandOutcome.Rejected, Reason: decision.Reason);
        }

        if (decision.State is not { ValueKind: JsonValueKind.Object } newState)
        {
            throw new InvalidOperationException($"The {room.GameType} rules accepted {command.Type} without a JSON object state.");
        }

        var action = room.ApplyCommand(
            player.Id,
            command,
            newState.GetRawText(),
            decision.EndsTurn,
            now,
            decision.Standings,
            decision.Finished,
            decision.AwaitsReplyFrom,
            decision.SkipsNextTurnOf);
        db.MatchActions.Add(action);
        if (room.Status == RoomStatus.Completed)
        {
            await db.RecordStatsAsync(room, progression.Value.Xp.ToRules(), cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new MatchCommandResult(MatchCommandOutcome.Applied, action, room.Version);
    }

    /// <summary>A game without abilities has no such command; a refusal by the engine is reported with its reason.</summary>
    private async Task<MatchCommandResult> UseAbilityAsync(
        GameRoom room,
        Player player,
        MatchCommand command,
        JsonElement? state,
        DateTimeOffset now,
        IDbContextTransaction transaction,
        CancellationToken cancellationToken)
    {
        if (abilities.CatalogFor(room.GameType) is null)
        {
            return new MatchCommandResult(MatchCommandOutcome.UnknownCommand);
        }

        // A turn waiting for another player's reply goes no further until it comes (DEC-058).
        if (room.ReplyingPlayerId is not null)
        {
            return new MatchCommandResult(MatchCommandOutcome.Rejected, Reason: ReplyPending);
        }

        var result = abilities.Use(new AbilityRequest(room, player.Id, command.Payload, state, random));
        if (!result.Used)
        {
            return new MatchCommandResult(MatchCommandOutcome.Rejected, Reason: result.Reason);
        }

        if (result.State is not { ValueKind: JsonValueKind.Object } newState)
        {
            throw new InvalidOperationException($"The {room.GameType} {result.Ability!.Id} handler returned no JSON object state.");
        }

        var action = room.ApplyAbility(player.Id, command, result.Ability!.Id, newState.GetRawText(), now, result.SkipsNextTurnOf);
        db.MatchActions.Add(action);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new MatchCommandResult(MatchCommandOutcome.Applied, action, room.Version);
    }

    /// <summary>Same player, type and payload; payloads are compared as JSON, so key order and spacing don't matter.</summary>
    private async Task<bool> IsSameActionAsync(
        MatchAction processed, Player player, string type, string payload, CancellationToken cancellationToken)
    {
        if (processed.PlayerId != player.Id || !string.Equals(processed.Type, type, StringComparison.Ordinal))
        {
            return false;
        }

        return await db.Database
            .SqlQuery<bool>($"""SELECT CAST({processed.Payload} AS jsonb) = CAST({payload} AS jsonb) AS "Value" """)
            .SingleAsync(cancellationToken);
    }
}
