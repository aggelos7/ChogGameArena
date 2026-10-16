using System.Text.Json;
using System.Text.Json.Serialization;
using ChogGames.Core.Matches;
using ChogGames.Core.Rooms;
using ChogGames.Games.ChogRace.Rules;

namespace ChogGames.Games.ChogRace;

/// <summary>
/// The finish, the time cap and results (STORY-056, RACE-008, RACE-009, ENG-011, DEC-060). A command resolves in full,
/// every move, jump, push and draw, before the finish is checked once: the player whose turn it is wins if their pawn
/// stands on the Finish, otherwise the first pawn their card pushed there, in the order the pushes resolved. At the
/// 20-minute cap the engine ends the match here, the furthest pawn first. Everyone else is ranked by tile, then by who
/// reached that tile first; pawns moved there by the same command, or never moved, share the rank. Each player still
/// playing gets a result, which the engine stores with their rank.
/// </summary>
public sealed partial class ChogRaceMatchRules
{
    public TimeSpan? TimeLimit => _current.TimeCap;

    /// <summary>
    /// The time is up (DEC-060): the match ends with the pawns where they are, the furthest first. A Play phase or an
    /// attack still waiting for its reply ends with it, the attack void (its card stays on the discard pile). A state this
    /// server can't play ranks everyone still playing together and is kept as it is.
    /// </summary>
    public MatchEnding EndMatch(MatchEndContext context)
    {
        var ending = context.TimeUp ? RaceEnding.TimeUp : RaceEnding.Finish;
        if (context.State is not { } json
            || Read(json) is not { } state
            || !_rulesByVersion.TryGetValue(state.RulesVersion, out var rules)
            || !IsPlayable(state, rules))
        {
            var together = context.Match.Members
                .Where(member => member is { HasLeft: false, Seat: not null })
                .Select(member => new MatchStanding(member.PlayerId, 1))
                .ToArray();
            var unchanged = context.State is { ValueKind: JsonValueKind.Object } kept
                ? kept.Clone()
                : JsonSerializer.SerializeToElement(new { ended = ending.ToString() });
            return new MatchEnding(unchanged, together);
        }

        var ended = state with { PlayPhase = null, Reply = null, Ended = ending };
        return new MatchEnding(JsonSerializer.SerializeToElement(ended, Json), Standings(ended, rules, context.Match));
    }

    /// <summary>
    /// A rank for every player still playing, with their result (DEC-060): the winner first, then by how far along the
    /// track they are, then by who reached that tile first. Pawns that got there in the same command, or never moved,
    /// share the rank (1, 2, 2, 4).
    /// </summary>
    private static IReadOnlyList<MatchStanding> Standings(RaceMatchState state, RaceRules rules, GameRoom match)
    {
        var pawns = state.Pawns.ToDictionary(pawn => pawn.Seat);
        var players = match.Members
            .Where(member => member is { HasLeft: false, Seat: not null })
            .Select(member => (member.PlayerId, Pawn: pawns.GetValueOrDefault(member.Seat!.Value)))
            .ToArray();
        bool Beats(RacePawn? a, RacePawn? b) =>
            a is not null
            && a.Seat != b?.Seat
            && b?.Seat != state.Winner
            && (a.Seat == state.Winner
                || b is null
                || a.Tile > b.Tile
                || (a.Tile == b.Tile && (a.Since ?? 0) < (b.Since ?? 0)));
        return players
            .Select(player => new MatchStanding(
                player.PlayerId,
                1 + players.Count(other => Beats(other.Pawn, player.Pawn)),
                player.Pawn is { } pawn ? JsonSerializer.SerializeToElement(ResultOf(pawn, rules), Json) : null))
            .ToArray();
    }

    /// <summary>A player's result (DEC-060): where their pawn ended, how far from the Finish, and their statistics.</summary>
    private static RaceResult ResultOf(RacePawn pawn, RaceRules rules)
    {
        var stats = pawn.Stats ?? new RaceStats();
        return new RaceResult(
            pawn.Tile,
            rules.Finish.Index - pawn.Tile,
            pawn.Tile == rules.Finish.Index,
            stats.CardsPlayed,
            stats.AttacksLanded,
            stats.AttacksBlocked);
    }

    /// <summary>
    /// <paramref name="after"/>, one command's outcome, with every pawn it moved stamped with the command's place in the
    /// match's order of moves, so a tie on a tile goes to whoever got there first (DEC-060). A command that moved nothing
    /// leaves the count as it is.
    /// </summary>
    private static RaceMatchState Arrived(RaceMatchState before, RaceMatchState after)
    {
        var tiles = before.Pawns.ToDictionary(pawn => pawn.Seat, pawn => pawn.Tile);
        if (after.Pawns.All(pawn => tiles.GetValueOrDefault(pawn.Seat, -1) == pawn.Tile))
        {
            return after;
        }

        var moves = (before.Moves ?? 0) + 1;
        return after with
        {
            Moves = moves,
            Pawns = after.Pawns.Select(pawn => tiles.GetValueOrDefault(pawn.Seat, -1) == pawn.Tile ? pawn : pawn with { Since = moves }).ToArray(),
        };
    }

    /// <summary>The seat's statistics after <paramref name="tally"/>; all zeros are stored as null.</summary>
    private static RaceMatchState Tallied(RaceMatchState state, int seat, Func<RaceStats, RaceStats> tally) => state with
    {
        Pawns = state.Pawns
            .Select(pawn => pawn.Seat == seat && tally(pawn.Stats ?? new RaceStats()) is var stats
                ? pawn with { Stats = stats == new RaceStats() ? null : stats }
                : pawn)
            .ToArray(),
    };

    private static RaceMatchState CardPlayed(RaceMatchState state, int seat) =>
        Tallied(state, seat, stats => stats with { CardsPlayed = stats.CardsPlayed + 1 });

    private static RaceMatchState AttackLanded(RaceMatchState state, int seat) =>
        Tallied(state, seat, stats => stats with { AttacksLanded = stats.AttacksLanded + 1 });

    private static RaceMatchState AttackBlocked(RaceMatchState state, int seat) =>
        Tallied(state, seat, stats => stats with { AttacksBlocked = stats.AttacksBlocked + 1 });

    /// <summary>How the race ended (DEC-060): a pawn reached the Finish, or the time cap.</summary>
    private enum RaceEnding
    {
        Finish,
        TimeUp,
    }

    /// <summary>
    /// A player's statistics so far, on their pawn and never in the board view (DEC-060): the cards they played, on their
    /// turn or HODL in reply; their attacks that landed (Rug, Liquidated or Whale not blocked, and Reverse pushing anyone
    /// back); and the attacks on them they blocked, with HODL or a Shield. Zero counts aren't stored.
    /// </summary>
    private sealed record RaceStats(
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] int CardsPlayed = 0,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] int AttacksLanded = 0,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] int AttacksBlocked = 0)
    {
        public bool IsValid() => CardsPlayed >= 0 && AttacksLanded >= 0 && AttacksBlocked >= 0;
    }

    /// <summary>
    /// A player's result, stored with their rank when the match ends (DEC-060): the tile their pawn ended on, the tiles it
    /// had left to the Finish, whether it stood on the Finish, and their statistics.
    /// </summary>
    private sealed record RaceResult(
        int Tile, int TilesLeft, bool Finished, int CardsPlayed, int AttacksLanded, int AttacksBlocked);
}
