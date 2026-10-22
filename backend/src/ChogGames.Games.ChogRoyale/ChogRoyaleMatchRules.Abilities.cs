using System.Text.Json;
using ChogGames.Core.Abilities;
using ChogGames.Games.ChogRoyale.Abilities;
using ChogGames.Games.ChogRoyale.Rules;

namespace ChogGames.Games.ChogRoyale;

/// <summary>
/// Chog Royale's abilities on the match state (STORY-080, STORY-093, ROYALE-039–044, DEC-091, DEC-095). The shared engine has already checked
/// the player locked the ability, has a use left and the round is open with their action not locked (DEC-081); a use is
/// recorded on the player for the round's resolution, which applies it and shows it in the ledger.
/// </summary>
public sealed partial class ChogRoyaleMatchRules
{
    /// <summary>
    /// <see cref="RoyaleAbilities.Deciding"/> while the match's current round is the state's, for a player still playing;
    /// otherwise none.
    /// </summary>
    public string? AbilityWindow(AbilityContext context) =>
        ForAbility(context) is { } use && use.State.Round == context.Turn ? RoyaleAbilities.Deciding : null;

    /// <summary>
    /// Null when the ability may be used now, otherwise why not: <c>last_round</c> for Alpha Leak in the last round, and
    /// <c>no_pump</c> for Double Pump without a Pump in hand.
    /// </summary>
    public string? CanUse(AbilityContext context)
    {
        if (ForAbility(context) is not { } use)
        {
            return Unavailable;
        }

        return context.Ability.Id switch
        {
            RoyaleAbilities.AlphaLeak when use.State.Round >= use.Rules.Rounds => "last_round",
            RoyaleAbilities.DoublePump when use.Player.Hand.All(card => card.Card != RoyaleCard.Pump) => "no_pump",
            _ => null,
        };
    }

    /// <summary>
    /// The state with the ability recorded on its player for the round, with its locked value; Alpha Leak also draws the
    /// next round's event now, from the server's randomness (DEC-091). Fresh Mint deals the player a new hand of as many
    /// cards now, one weighted draw each, and drops an unlocked card choice, which named a card no longer held; Insider
    /// Info records whom it looks at (STORY-093, DEC-095).
    /// </summary>
    public JsonElement Use(AbilityContext context)
    {
        var (state, rules, player) = ForAbility(context) ?? throw new InvalidOperationException("Only a usable ability is used.");
        var id = context.Ability.Id;
        var value = id switch
        {
            RoyaleAbilities.DiamondHandsPlus => context.Ability.Parameters[RoyaleAbilities.Blocks],
            RoyaleAbilities.DoublePump => context.Ability.Parameters[RoyaleAbilities.Percent],
            RoyaleAbilities.StopLoss => context.Ability.Parameters[RoyaleAbilities.Mon],
            _ => 0,
        };
        var used = player with { Ability = new RoyaleAbilityUse(id, (int)value, id == RoyaleAbilities.InsiderInfo ? context.Target!.Seat : null) };
        var nextCard = state.NextCard;
        if (id == RoyaleAbilities.FreshMint)
        {
            used = used with
            {
                Hand = Deal(rules, player.Hand.Length, ref nextCard, context.Random),
                Action = player.Action is { Card: not null } ? null : player.Action,
            };
        }

        var players = state.Players.Select(p => p.Seat == player.Seat ? used : p).ToArray();
        var nextEvent = id == RoyaleAbilities.AlphaLeak ? state.NextEvent ?? DrawEvent(rules, context.Random) : state.NextEvent;
        return Serialize(state with { Players = players, NextCard = nextCard, NextEvent = nextEvent });
    }

    /// <summary>A playable state with the ability's player in it, still playing and without an ability used this round.</summary>
    private (RoyaleMatchState State, RoyaleRules Rules, RoyalePlayer Player)? ForAbility(AbilityContext context)
    {
        if (Readable(context.State) is not { } state
            || !_rulesByVersion.TryGetValue(state.RulesVersion, out var rules)
            || context.Players.FirstOrDefault(p => p.PlayerId == context.PlayerId) is not { IsPlaying: true } user
            || state.Players.FirstOrDefault(p => p.Seat == user.Seat) is not { Ability: null } player)
        {
            return null;
        }

        return (state, rules, player);
    }
}
