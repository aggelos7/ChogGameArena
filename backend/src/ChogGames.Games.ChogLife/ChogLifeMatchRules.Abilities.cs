using System.Text.Json;
using ChogGames.Core.Abilities;
using ChogGames.Core.Matches;
using ChogGames.Games.ChogLife.Abilities;
using ChogGames.Games.ChogLife.Rules;

namespace ChogGames.Games.ChogLife;

/// <summary>
/// Chog Life's abilities on the match state (LIFE-008, STORY-042, DEC-044); <see cref="LifeAbilities"/> describes them.
/// The shared engine has already checked the player, their locked ability, its uses and that it's their turn.
/// </summary>
public sealed partial class ChogLifeMatchRules
{
    /// <summary>A preview takes the die and then two 0–99 draws: a stake's payout and the tile's event.</summary>
    private const int PreviewDrawCount = 3;

    /// <summary>
    /// <see cref="LifeAbilities.AtFork"/> while the player chooses a lane, otherwise <see cref="LifeAbilities.BeforeRoll"/>;
    /// none while they decide whether to keep a previewed lane, once they have finished or the match has ended, or for a
    /// state this server can't play.
    /// </summary>
    internal string? AbilityWindow(AbilityContext context)
    {
        if (AbilityPawn(context) is not var (state, pawn, rules)
            || state.Ended is not null
            || pawn.Tile == rules.Finish.Index
            || state.Finished?.Contains(pawn.Seat) == true
            || state.Confirming == pawn.Seat)
        {
            return null;
        }

        return state.Choosing == pawn.Seat ? LifeAbilities.AtFork : LifeAbilities.BeforeRoll;
    }

    /// <summary>One ability is armed at a time; arming another before it acts is refused.</summary>
    internal string? CanArm(AbilityContext context) => AbilityPawn(context) switch
    {
        null => "game_state_unavailable",
        { Pawn.Armed: not null } => "ability_armed",
        _ => null,
    };

    /// <summary>The pawn carries the ability, with its locked percent or draws, until the first outcome it fits.</summary>
    internal JsonElement Arm(AbilityContext context)
    {
        var (state, pawn, _) = AbilityPawn(context)!.Value;
        var parameters = context.Ability.Parameters;
        var percent = parameters.TryGetValue(LifeAbilities.Percent, out var value) ? value : (decimal?)null;
        var draws = parameters.TryGetValue(LifeAbilities.Draws, out var times) ? (int)times : (int?)null;
        var armed = pawn with { Armed = new LifeArmed(context.Ability.Id, percent, draws) };
        return JsonSerializer.SerializeToElement(state with { Pawns = Replace(state, armed) }, Json);
    }

    /// <summary>
    /// The lane just chosen with Event Preview armed: the pawn's next move on it is drawn now, from the server's source and
    /// in the order the roll uses them (the die, then two 0–99 draws for a stake's payout and the tile's event), and the
    /// ability is spent. The roll replays the draws, so what the player sees is what happens; nobody else sees them.
    /// </summary>
    private static LifeMatchState Previewed(LifeMatchState state, int seat, LifeRules rules, IRandomSource random)
    {
        var pawn = state.Pawns.First(p => p.Seat == seat);
        int[] drawn = [random.Next(1, rules.DieFaces + 1), random.Next(0, 100), random.Next(0, 100)];
        return state with { Pawns = Replace(state, pawn with { Armed = null, Preview = drawn }) };
    }

    /// <summary>
    /// After a preview, keeping the lane keeps the previewed move; switching to the other lane moves the stake taken at
    /// this fork with it and throws the preview away, so the next roll is drawn afresh (DEC-044).
    /// </summary>
    private static LifeMatchState KeepOrSwitch(LifeMatchState state, LifePawn pawn, LifeLane lane)
    {
        if (lane == pawn.Lane)
        {
            return state;
        }

        var stake = pawn.Stake is { } held && held.Lane == pawn.Lane ? held with { Lane = lane } : pawn.Stake;
        var switched = pawn with { Lane = lane, Stake = stake, Preview = null };
        return state with
        {
            Pawns = Replace(state, switched),
            LastMove = state.LastMove is { } move && move.Seat == pawn.Seat
                ? move with { Lane = lane, Switched = true }
                : state.LastMove,
        };
    }

    /// <summary>The state and the ability user's pawn, when this server can play them.</summary>
    private (LifeMatchState State, LifePawn Pawn, LifeRules Rules)? AbilityPawn(AbilityContext context)
    {
        var seat = context.Players.FirstOrDefault(player => player.PlayerId == context.PlayerId)?.Seat;
        if (context.State is not { } json
            || Read(json) is not { } stored
            || !_rulesByVersion.TryGetValue(stored.RulesVersion, out var rules))
        {
            return null;
        }

        var state = WithMon(stored, rules);
        return state.Pawns.FirstOrDefault(pawn => pawn.Seat == seat) is { } found && IsPlayable(found, rules)
            ? (state, found, rules)
            : null;
    }

    /// <summary>
    /// What the tile <paramref name="tile"/> moves, changed by the pawn's <paramref name="armed"/> ability if it fits: the
    /// boost a gain earns, the share of a loss still taken, or the best of the locked draws (2 before STORY-090) of an
    /// event with several outcomes, which reports the worst one. The ability is spent once it acts and stays armed
    /// otherwise.
    /// </summary>
    private static LifeResolution Resolve(LifeRules rules, int tile, int liquid, LifeArmed? armed, IRandomSource random)
    {
        var first = rules.Resolve(tile, liquid, random);
        if (armed is null || first is not { } change)
        {
            return new LifeResolution(first, armed);
        }

        switch (armed.Ability)
        {
            case LifeAbilities.EventReroll
                when rules.Tiles[tile] is { Type: LifeTileType.Event, Event: { } kind } && rules.Events[kind].Outcomes.Count > 1:
                var draws = armed.Draws ?? DefaultDraws;
                var drawn = new List<int>(draws) { change };
                while (drawn.Count < draws)
                {
                    drawn.Add(rules.Resolve(tile, liquid, random)!.Value);
                }

                return new LifeResolution(drawn.Max(), null, Rerolled: drawn.Min(), Draws: draws);
            case LifeAbilities.RewardBoost when change > 0:
                return new LifeResolution(change, null, Boost: (int)(change * armed.Percent!.Value / 100));
            case LifeAbilities.LossProtection when change < 0:
                var saved = (int)(-change * (100 - armed.Percent!.Value) / 100);
                return new LifeResolution(change + saved, null, Saved: saved);
            default:
                return new LifeResolution(change, armed);
        }
    }

    /// <summary>
    /// What the pawn's previewed next move on its chosen lane does, exactly as the roll will play it, for its player only:
    /// the die, the tiles passed and, on an event tile, the outcome drawn, as its rule. A stake's payout is drawn in its
    /// place but never shown (DEC-044). Null without a preview.
    /// </summary>
    private static LifePreview? PreviewFor(LifeMatchState state, int seat, LifeRules rules)
    {
        if (state.Ended is not null
            || state.Pawns.FirstOrDefault(pawn => pawn.Seat == seat) is not { Preview: { } drawn, Lane: { } lane } pawn
            || !IsPlayable(pawn, rules))
        {
            return null;
        }

        var random = new PreviewDraws(drawn, NoDraws.Instance);
        var roll = random.Next(1, rules.DieFaces + 1);
        var path = rules.Path(pawn.Tile, roll, lane);
        if (pawn.Stake is { } stake && path.Any(tile => rules.Tiles[tile].Lane == LifeLane.Main))
        {
            rules.Payout(stake, random);
        }

        var landed = rules.Tiles[path[^1]];
        var outcome = landed is { Type: LifeTileType.Event, Event: { } kind } ? rules.Events[kind].Draw(random) : null;
        return new LifePreview(lane, roll, path[^1], path, outcome);
    }

    /// <summary>A previewed move: the lane, die, destination and tiles passed, and the event outcome drawn there, if any.</summary>
    private sealed record LifePreview(LifeLane Lane, int Roll, int To, IReadOnlyList<int> Path, LifeEventOutcome? Outcome);

    /// <summary>Preview draws in range, held on a fork after its lane was chosen.</summary>
    private static bool IsPreviewValid(LifePawn pawn, IReadOnlyList<int> drawn, LifeRules rules) =>
        rules.Tiles[pawn.Tile].Type == LifeTileType.Branch
        && pawn.Lane is not null
        && drawn.Count == PreviewDrawCount
        && drawn[0] >= 1 && drawn[0] <= rules.DieFaces
        && drawn.Skip(1).All(chance => chance is >= 0 and < 100);

    /// <summary>Lucky Reroll's draws in states armed before it had a value (STORY-090).</summary>
    private const int DefaultDraws = 2;

    /// <summary>
    /// An ability armed until it acts: its id and, for Reward Boost and Loss Protection, the percent locked at start, or for
    /// Lucky Reroll its draws (null in states from before STORY-090: 2). Event Preview is armed only on a fork, before its
    /// lane is chosen.
    /// </summary>
    private sealed record LifeArmed(string Ability, decimal? Percent = null, int? Draws = null)
    {
        public bool IsValid() => Ability switch
        {
            LifeAbilities.RewardBoost or LifeAbilities.LossProtection => Percent is >= 1 and <= 100 && Draws is null,
            LifeAbilities.EventReroll => Percent is null && Draws is null or (>= 2 and <= LifeAbilities.MaxDraws),
            LifeAbilities.EventPreview => Percent is null && Draws is null,
            _ => false,
        };
    }

    /// <param name="Change">The signed MON the tile moves, null for a tile that does nothing.</param>
    /// <param name="Armed">The ability still armed after it, null once it acted.</param>
    private sealed record LifeResolution(
        int? Change, LifeArmed? Armed, int? Boost = null, int? Saved = null, int? Rerolled = null, int? Draws = null);

    /// <summary>Plays back a preview's draws in order, then the server's source; a draw that doesn't fit is a bug.</summary>
    private sealed class PreviewDraws(IReadOnlyList<int> drawn, IRandomSource then) : IRandomSource
    {
        private int _next;

        public int Next(int minInclusive, int maxExclusive)
        {
            if (_next >= drawn.Count)
            {
                return then.Next(minInclusive, maxExclusive);
            }

            var value = drawn[_next++];
            return value >= minInclusive && value < maxExclusive
                ? value
                : throw new InvalidOperationException("A previewed draw doesn't fit the roll.");
        }
    }

    /// <summary>For showing a preview: every draw it needs is stored, so asking for another is a bug.</summary>
    private sealed class NoDraws : IRandomSource
    {
        public static readonly NoDraws Instance = new();

        public int Next(int minInclusive, int maxExclusive) =>
            throw new InvalidOperationException("A preview needs no draw beyond those it stored.");
    }
}
