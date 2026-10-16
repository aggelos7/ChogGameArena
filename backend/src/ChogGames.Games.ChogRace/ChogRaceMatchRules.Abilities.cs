using System.Text.Json;
using ChogGames.Core.Abilities;
using ChogGames.Games.ChogRace.Abilities;
using ChogGames.Games.ChogRace.Rules;

namespace ChogGames.Games.ChogRace;

/// <summary>
/// Chog Race's abilities on the match state (RACE-007, STORY-055, DEC-059); <see cref="RaceAbilities"/> describes them.
/// The shared engine has already checked the player, their locked ability, its uses, the target and that it's their
/// turn with no reply awaited.
/// </summary>
public sealed partial class ChogRaceMatchRules
{
    /// <summary>
    /// <see cref="RaceAbilities.AfterRoll"/> in the player's Play phase, otherwise <see cref="RaceAbilities.BeforeRoll"/>;
    /// none once the race is won, or for a state this server can't play. While an attack waits for a reply the engine
    /// refuses every ability first (<c>reply_pending</c>, DEC-058), so a reply left from a window a leaver closed counts for
    /// nothing here.
    /// </summary>
    internal string? AbilityWindow(AbilityContext context)
    {
        if (AbilityPawn(context) is not var (state, pawn, _) || state.Winner is not null)
        {
            return null;
        }

        return state.PlayPhase == pawn.Seat ? RaceAbilities.AfterRoll : RaceAbilities.BeforeRoll;
    }

    /// <summary>
    /// Card Vision needs a card in the deck (<c>deck_empty</c>); Card Steal needs room in the player's hand
    /// (<c>hand_full</c>) and a card in the target's (<c>invalid_target</c>).
    /// </summary>
    internal string? CanUse(AbilityContext context)
    {
        if (AbilityPawn(context) is not var (state, pawn, rules))
        {
            return "game_state_unavailable";
        }

        return context.Ability.Id switch
        {
            RaceAbilities.CardPreview when (state.Deck?.Count ?? 0) == 0 => "deck_empty",
            RaceAbilities.CardSteal when HandOf(state, pawn.Seat).Count >= rules.MaxHand => "hand_full",
            RaceAbilities.CardSteal when context.Target is null || HandOf(state, context.Target.Seat).Count == 0 => "invalid_target",
            _ => null,
        };
    }

    /// <summary>
    /// Speed Boost arms the pawn with its locked tiles for the next roll; Shield raises it until an attack it blocks; Card
    /// Vision lets the player see the deck's top card until a draw; Card Steal moves one of the target's cards, drawn at
    /// random by the server, to the player's hand. The use is kept as the match's latest, for everyone to see, the stolen
    /// card only for the two players.
    /// </summary>
    internal JsonElement Use(AbilityContext context)
    {
        var (state, pawn, _) = AbilityPawn(context)!.Value;
        var used = new RaceAbilityUse(pawn.Seat, context.Ability.Id);
        var pawns = state.Pawns;
        switch (context.Ability.Id)
        {
            case RaceAbilities.MovementBoost:
                var tiles = (int)context.Ability.Parameters[RaceAbilities.Tiles];
                pawns = Replace(pawns, pawn with { Boost = tiles });
                break;

            case RaceAbilities.AttackBlock:
                pawns = Replace(pawns, pawn with { Shielded = true });
                break;

            case RaceAbilities.CardPreview:
                pawns = Replace(pawns, pawn with { Sees = true });
                break;

            case RaceAbilities.CardSteal:
                var target = state.Pawns.First(p => p.Seat == context.Target!.Seat);
                var theirs = target.Hand!.ToList();
                var card = theirs[context.Random.Next(0, theirs.Count)];
                theirs.Remove(card);
                pawns = Replace(pawns, target with { Hand = theirs.Count == 0 ? null : theirs });
                pawns = Replace(pawns, pawn with { Hand = [.. pawn.Hand ?? [], card] });
                used = used with { Target = target.Seat, Card = card };
                break;
        }

        return JsonSerializer.SerializeToElement(state with { Pawns = pawns, Ability = used }, Json);
    }

    /// <summary>The state and the ability user's pawn, when this server can play them.</summary>
    private (RaceMatchState State, RacePawn Pawn, RaceRules Rules)? AbilityPawn(AbilityContext context)
    {
        var seat = context.Players.FirstOrDefault(player => player.PlayerId == context.PlayerId)?.Seat;
        if (context.State is not { } json
            || Read(json) is not { } state
            || !_rulesByVersion.TryGetValue(state.RulesVersion, out var rules)
            || !IsPlayable(state, rules))
        {
            return null;
        }

        return state.Pawns.FirstOrDefault(pawn => pawn.Seat == seat) is { } found ? (state, found, rules) : null;
    }

    private static IReadOnlyList<RacePawn> Replace(IReadOnlyList<RacePawn> pawns, RacePawn pawn) =>
        pawns.Select(p => p.Seat == pawn.Seat ? pawn : p).ToArray();

    /// <summary>
    /// The latest ability used (DEC-059): who used which and, for Card Steal, from whom and the card, which only those two
    /// players see. It's kept until another player rolls or uses an ability.
    /// </summary>
    private sealed record RaceAbilityUse(int Seat, string Ability, int? Target = null, RaceCard? Card = null);
}
