using System.Text.Json;
using ChogGames.Core.Matches;

namespace ChogGames.Core.Abilities;

/// <summary>
/// A game's implementation of one ability (STORY-030, NFT-014, DEC-032). The engine has already checked that the ability
/// exists, that the target fits its <see cref="AbilityTargetKind"/> and that the match is in one of its windows; the
/// handler adds the game's own conditions and applies the effect on the server. Like match rules, handlers are
/// deterministic over their inputs, keep no state and draw any chance from the context's <see cref="IRandomSource"/>.
/// They see players only as ids and seats, never characters or NFT traits.
/// </summary>
public interface IAbilityHandler
{
    /// <summary>The <see cref="AbilityDefinition.Id"/> this handler implements.</summary>
    string AbilityId { get; }

    /// <summary>The values its definition must configure, each within range; the catalog refuses any other set.</summary>
    IReadOnlyList<AbilityParameter> Parameters { get; }

    /// <summary>
    /// Null when the ability may be used now, otherwise a stable, lower-case reason such as <c>no_reward_pending</c>.
    /// Nothing changes when it refuses.
    /// </summary>
    string? CanExecute(AbilityContext context);

    /// <summary>The game state after the effect; only called after <see cref="CanExecute"/> allowed it.</summary>
    JsonElement Execute(AbilityContext context);

    /// <summary>
    /// The effect as the engine applies it: <see cref="Execute"/>'s state and, for an ability that makes an opponent skip
    /// their next turn, who (STORY-092, DEC-080). Only called after <see cref="CanExecute"/> allowed it.
    /// </summary>
    AbilityEffect Apply(AbilityContext context) => new(Execute(context));
}

/// <param name="State">The game state after the effect.</param>
/// <param name="SkipsNextTurnOf">
/// A player still playing, other than the ability's user, who skips their next turn once, as the shared engine's skip
/// does for game commands (DEC-058); null for none.
/// </param>
public sealed record AbilityEffect(JsonElement State, Guid? SkipsNextTurnOf = null);

/// <summary>
/// The game's activation windows (STORY-030): which moment of the game the match is in for a player, such as
/// <c>before_event</c>, so the engine can check it against the ability's <see cref="AbilityDefinition.Windows"/>.
/// </summary>
public interface IAbilityTiming
{
    /// <summary>The window the match is in for <see cref="AbilityContext.PlayerId"/>, or null when no ability may be used.</summary>
    string? CurrentWindow(AbilityContext context);
}

/// <summary>A configured value an ability's effect needs, with the range a definition may give it.</summary>
/// <param name="Default">
/// For a parameter added after matches locked the ability: the value those matches use, as the effect was before
/// (STORY-090). A catalog's definitions still configure every parameter.
/// </param>
public sealed record AbilityParameter(string Name, decimal Min, decimal Max, decimal? Default = null);

/// <summary>A player of the match as abilities see them: who they are and where they sit, nothing about their character.</summary>
/// <param name="Seat">Turn order from 1; targets are named by seat, since player ids never reach clients.</param>
/// <param name="IsPlaying">False once the player has left or forfeited.</param>
/// <param name="SkipsNextTurn">True while the player is due to skip their next turn (DEC-058, STORY-092).</param>
public sealed record AbilityPlayer(Guid PlayerId, int Seat, bool IsPlaying, bool SkipsNextTurn = false);

/// <param name="GameType">The match's game.</param>
/// <param name="PlayerId">The session's player using the ability.</param>
/// <param name="Ability">The ability's definition, with its configured values.</param>
/// <param name="Target">The targeted opponent for <see cref="AbilityTargetKind.Opponent"/>, otherwise null.</param>
/// <param name="Players">Every seated player of the match, in seat order.</param>
/// <param name="State">The game's state, or null before its first accepted command.</param>
/// <param name="Random">The server's source of chance: the only place random targets, cards or rerolls come from.</param>
/// <param name="Turn">The match's turn number, which tells one turn from the next (STORY-092).</param>
public sealed record AbilityContext(
    string GameType,
    Guid PlayerId,
    AbilityDefinition Ability,
    AbilityPlayer? Target,
    IReadOnlyList<AbilityPlayer> Players,
    JsonElement? State,
    IRandomSource Random,
    int Turn = 0);
