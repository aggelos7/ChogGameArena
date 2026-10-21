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
    /// (<c>hand_full</c>) and a card in the target's (<c>invalid_target</c>). Double Dice rolls once before the roll
    /// (<c>dice_rolled</c>); Freeze needs a target not already due to skip (<c>already_skipping</c>) and is used at most
    /// once a turn (<c>froze_this_turn</c>) (STORY-092, DEC-080).
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
            RaceAbilities.DoubleDice when pawn.Dice is not null => "dice_rolled",
            RaceAbilities.Freeze when context.Target is null => "invalid_target",
            RaceAbilities.Freeze when context.Target.SkipsNextTurn => "already_skipping",
            RaceAbilities.Freeze when pawn.FrozeOn == context.Turn => "froze_this_turn",
            _ => null,
        };
    }

    /// <summary>
    /// Speed Boost arms the pawn with its locked tiles for the next roll; Shield raises it until it has blocked its locked
    /// attacks; Card Vision lets the player see the deck's locked number of top cards until a draw; Card Steal moves up to
    /// its locked number of the target's cards, each drawn at random by the server, to the player's hand while it has room
    /// (DEC-077). Double Dice rolls its locked number of dice now, from the server's source, and the pawn holds them until
    /// its roll; Freeze marks the turn it was used on, and the engine makes the target skip their next turn (STORY-092,
    /// DEC-080). The use is kept as the match's latest, for everyone to see, the stolen cards only for the two players.
    /// </summary>
    internal JsonElement Use(AbilityContext context)
    {
        var (state, pawn, rules) = AbilityPawn(context)!.Value;
        var used = new RaceAbilityUse(pawn.Seat, context.Ability.Id);
        var pawns = state.Pawns;
        switch (context.Ability.Id)
        {
            case RaceAbilities.MovementBoost:
                var tiles = (int)context.Ability.Parameters[RaceAbilities.Tiles];
                pawns = Replace(pawns, pawn with { Boost = tiles });
                break;

            case RaceAbilities.AttackBlock:
                pawns = Replace(pawns, pawn with { Shielded = true, Blocks = Count(context, RaceAbilities.Blocks) });
                break;

            case RaceAbilities.CardPreview:
                pawns = Replace(pawns, pawn with { Sees = true, SeesCards = Count(context, RaceAbilities.Cards) });
                break;

            case RaceAbilities.CardSteal:
                var target = state.Pawns.First(p => p.Seat == context.Target!.Seat);
                var theirs = target.Hand!.ToList();
                var mine = (pawn.Hand ?? []).ToList();
                var taken = new List<RaceCard>();
                while (taken.Count < Count(context, RaceAbilities.Cards) && theirs.Count > 0 && mine.Count < rules.MaxHand)
                {
                    var card = theirs[context.Random.Next(0, theirs.Count)];
                    theirs.Remove(card);
                    mine.Add(card);
                    taken.Add(card);
                }

                pawns = Replace(pawns, target with { Hand = theirs.Count == 0 ? null : theirs });
                pawns = Replace(pawns, pawn with { Hand = mine });
                used = used with { Target = target.Seat, Card = taken[0], Cards = taken, Taken = taken.Count };
                break;

            case RaceAbilities.DoubleDice:
                var dice = Enumerable.Range(0, Count(context, RaceAbilities.Dice))
                    .Select(_ => context.Random.Next(rules.MinMove, rules.MaxMove + 1))
                    .ToArray();
                pawns = Replace(pawns, pawn with { Dice = dice });
                break;

            case RaceAbilities.Freeze:
                pawns = Replace(pawns, pawn with { FrozeOn = context.Turn });
                used = used with { Target = context.Target!.Seat };
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

    /// <summary>A whole-number value the ability locked, such as Shield's <c>blocks</c>.</summary>
    private static int Count(AbilityContext context, string parameter) => (int)context.Ability.Parameters[parameter];

    /// <summary>Double Dice's dice in the move range, and the die kept when a turn ran out one of them (STORY-092, DEC-080).</summary>
    private static bool AreDiceValid(RacePawn pawn, RaceRules rules) =>
        pawn.Dice is null
            ? pawn.Kept is null
            : pawn.Dice.Count is >= 2 and <= RaceAbilities.MaxDice
                && pawn.Dice.All(die => die >= rules.MinMove && die <= rules.MaxMove)
                && (pawn.Kept is null || pawn.Dice.Contains(pawn.Kept.Value));

    /// <summary>The die a <c>ROLL</c> keeps of Double Dice's, <c>{"die": 3}</c>; null when the payload names none.</summary>
    private static int? DieIn(JsonElement payload) =>
        payload.ValueKind == JsonValueKind.Object
        && payload.TryGetProperty("die", out var die)
        && die.ValueKind == JsonValueKind.Number
        && die.TryGetInt32(out var value)
            ? value
            : null;

    /// <summary>A Shield that blocked an attack: lowered once it has no blocks left (DEC-059, DEC-077).</summary>
    private static RacePawn ShieldBlocked(RacePawn pawn) =>
        (pawn.Blocks ?? 1) > 1
            ? pawn with { Blocks = pawn.Blocks - 1 }
            : pawn with { Shielded = null, Blocks = null };

    /// <summary>
    /// The latest ability used (DEC-059): who used which and, for Card Steal, from whom and the cards taken (the first as
    /// <paramref name="Card"/>, as before STORY-090), which only those two players see, and how many, which everyone
    /// sees. It's kept until another player rolls or uses an ability.
    /// </summary>
    private sealed record RaceAbilityUse(int Seat, string Ability, int? Target = null, RaceCard? Card = null)
    {
        public IReadOnlyList<RaceCard>? Cards { get; init; }

        /// <summary>How many cards Card Steal took; null in states from before STORY-090: 1.</summary>
        public int? Taken { get; init; }
    }
}
