using System.Text.Json;
using ChogGames.Core.Games;
using ChogGames.Core.Matches;
using ChogGames.Core.Players;
using ChogGames.Core.Rooms;
using ChogGames.Games.ChogRace.Rules;
using static ChogGames.Games.ChogRace.Rules.RaceTile;

namespace ChogGames.Games.ChogRace.Tests;

/// <summary>
/// STORY-056, RACE-008, RACE-009, DEC-060: a command resolves in full before the finish is checked once, the player
/// whose turn it is first, then the pawns their card pushed in seat order. The 20-minute cap ends the match with the
/// furthest pawn first. Players on the same tile are ranked by who got there first; pawns moved there by the same command,
/// or never moved, share the rank. Every player still playing gets a result: their tile, tiles left, whether they
/// finished, the cards they played, their attacks that landed and the attacks on them they blocked.
/// </summary>
public class ChogRaceFinishTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);

    private static readonly ChogRaceMatchRules Rules = new([RaceRulesV1.Rules]);

    private sealed class KeepOrder : IRandomSource
    {
        public int Next(int minInclusive, int maxExclusive) => maxExclusive - 1;
    }

    private sealed class NoDraws : IRandomSource
    {
        public int Next(int minInclusive, int maxExclusive) => throw new InvalidOperationException("Nothing should be drawn.");
    }

    private sealed class Dice(params int[] rolls) : IRandomSource
    {
        private readonly Queue<int> _rolls = new(rolls);

        public int Next(int minInclusive, int maxExclusive) => _rolls.Dequeue();
    }

    private static (GameRoom Room, Player[] Players) Started(int count, ChogRaceMatchRules? rules = null)
    {
        rules ??= Rules;
        var players = Enumerable.Range(1, count)
            .Select(n => PlayerDisplayName.TryCreate($"Player {n}", out var name) ? Player.CreateGuest(name, Now) : throw new InvalidOperationException())
            .ToArray();
        Assert.True(RoomCode.TryParse("R4CE7Q", out var code));
        var room = GameRoom.Create(GameDefinition.Create(GameDefinition.ChogRace, 2, 6), players[0], code, Now);
        for (var i = 1; i < count; i++)
        {
            room.Join(players[i], Now.AddSeconds(i));
        }

        foreach (var player in players)
        {
            room.MarkReady(player);
        }

        var random = new KeepOrder();
        room.Start(
            players[0].Id,
            players,
            players.ToDictionary(p => p.Id, _ => CharacterSnapshot.Standard(DefaultAvatar.Classic, Now)),
            TimeSpan.FromSeconds(20),
            random,
            Now.AddMinutes(1),
            startState: started => rules.StartState(new MatchStartContext(started, random))?.GetRawText(),
            timeLimit: rules.TimeLimit);
        return (room, players);
    }

    /// <summary>A version 1 state with the given pawns (JSON objects), counting <paramref name="moves"/> moves so far.</summary>
    private static string State(int moves, string extra, params string[] pawns) =>
        $$"""{"rulesVersion":1,"pawns":[{{string.Join(",", pawns)}}],"deck":["Pump"],"discard":[]{{(moves > 0 ? $",\"moves\":{moves}" : "")}}{{extra}}}""";

    private static string Pawn(int seat, int tile, int? since = null, string extra = "") =>
        $$"""{"seat":{{seat}},"tile":{{tile}}{{(since is { } s ? $",\"since\":{s}" : "")}}{{extra}}}""";

    private const string Rolled = ""","lastMove":{"seat":1,"roll":1,"from":0,"landed":1,"to":1},"playPhase":1""";

    private static MatchRuleDecision Apply(
        GameRoom room, Guid player, string type, string state, IRandomSource random, string payload = "{}", ChogRaceMatchRules? rules = null)
    {
        using var stateJson = JsonDocument.Parse(state);
        using var payloadJson = JsonDocument.Parse(payload);
        var decision = (rules ?? Rules).Apply(new MatchCommandContext(room, player, type, payloadJson.RootElement, stateJson.RootElement, random));
        return decision with { State = decision.State?.Clone() };
    }

    private static MatchEnding EndMatch(GameRoom room, string? state, bool timeUp = true)
    {
        using var document = state is null ? null : JsonDocument.Parse(state);
        var ending = Rules.EndMatch(new MatchEndContext(room, document?.RootElement, timeUp));
        return ending with { State = ending.State.Clone(), Standings = ending.Standings.Select(s => s with { Result = s.Result?.Clone() }).ToArray() };
    }

    private static (Guid, int)[] Ranks(IReadOnlyList<MatchStanding> standings) => standings.Select(s => (s.PlayerId, s.Rank)).ToArray();

    private static JsonElement PawnOf(JsonElement state, int seat) =>
        state.GetProperty("pawns").EnumerateArray().Single(pawn => pawn.GetProperty("seat").GetInt32() == seat);

    private static string? StatsOf(JsonElement state, int seat) =>
        PawnOf(state, seat).TryGetProperty("stats", out var stats) ? stats.GetRawText() : null;

    private static string ResultOf(IReadOnlyList<MatchStanding> standings, Guid player) =>
        standings.Single(s => s.PlayerId == player).Result!.Value.GetRawText();

    [Fact]
    public void The_match_is_capped_at_the_rules_twenty_minutes()
    {
        Assert.Equal(TimeSpan.FromMinutes(20), ((IMatchRules)Rules).TimeLimit);

        var (room, _) = Started(2);

        Assert.Equal(room.StartedAt!.Value.AddMinutes(20), room.MatchEndsAt);
    }

    [Fact]
    public void Every_move_is_stamped_so_the_pawn_that_reached_a_tile_first_is_known()
    {
        var (room, players) = Started(3);

        var rolled = Apply(room, players[0].Id, ChogRaceMatchRules.Roll, State(4, "", Pawn(1, 8, 2), Pawn(2, 10, 3), Pawn(3, 0)), new Dice(1));

        Assert.Equal(5, rolled.State!.Value.GetProperty("moves").GetInt32());
        Assert.Equal(5, PawnOf(rolled.State.Value, 1).GetProperty("since").GetInt32());
        Assert.Equal(3, PawnOf(rolled.State.Value, 2).GetProperty("since").GetInt32());
        Assert.False(PawnOf(rolled.State.Value, 3).TryGetProperty("since", out _));
    }

    [Fact]
    public void Pawns_one_reverse_pushes_share_the_commands_stamp_and_a_pawn_it_leaves_keeps_its_own()
    {
        var (room, players) = Started(4);
        var state = State(6, Rolled, Pawn(1, 20, 6, ""","hand":["Reverse"]"""), Pawn(2, 11, 2), Pawn(3, 18, 5), Pawn(4, 0));

        var decision = Apply(room, players[0].Id, ChogRaceMatchRules.PlayCard, state, new NoDraws(), """{"card":"Reverse"}""");

        var next = decision.State!.Value;
        Assert.Equal(7, next.GetProperty("moves").GetInt32());
        Assert.Equal(6, PawnOf(next, 1).GetProperty("since").GetInt32());
        Assert.Equal(7, PawnOf(next, 2).GetProperty("since").GetInt32());
        Assert.Equal(7, PawnOf(next, 3).GetProperty("since").GetInt32());
        Assert.False(PawnOf(next, 4).TryGetProperty("since", out _));
    }

    [Fact]
    public void Ending_the_turn_moves_nothing_and_counts_no_move()
    {
        var (room, players) = Started(2);

        var decision = Apply(room, players[0].Id, ChogRaceMatchRules.EndTurn, State(3, Rolled, Pawn(1, 1, 3), Pawn(2, 4, 2)), new NoDraws());

        Assert.Equal(3, decision.State!.Value.GetProperty("moves").GetInt32());
    }

    [Fact]
    public void The_winner_ranks_first_then_the_rest_by_tile_then_by_who_reached_it_first()
    {
        var (room, players) = Started(5);
        var state = State(9, "", Pawn(1, 26, 9), Pawn(2, 10, 7), Pawn(3, 10, 3), Pawn(4, 12, 8), Pawn(5, 0));

        var decision = Apply(room, players[0].Id, ChogRaceMatchRules.Roll, state, new Dice(1));

        Assert.Equal(MatchRuleOutcome.Accepted, decision.Outcome);
        Assert.Equal(1, decision.State!.Value.GetProperty("winner").GetInt32());
        Assert.Equal("Finish", decision.State.Value.GetProperty("ended").GetString());
        Assert.Equal(
            [(players[0].Id, 1), (players[1].Id, 4), (players[2].Id, 3), (players[3].Id, 2), (players[4].Id, 5)],
            Ranks(decision.Standings!));
    }

    [Fact]
    public void Pawns_moved_onto_a_tile_by_the_same_command_or_never_moved_share_their_rank()
    {
        var (room, players) = Started(5);
        var state = State(9, "", Pawn(1, 26, 9), Pawn(2, 10, 4), Pawn(3, 10, 4), Pawn(4, 0), Pawn(5, 0));

        var decision = Apply(room, players[0].Id, ChogRaceMatchRules.Roll, state, new Dice(1));

        Assert.Equal(
            [(players[0].Id, 1), (players[1].Id, 2), (players[2].Id, 2), (players[3].Id, 4), (players[4].Id, 4)],
            Ranks(decision.Standings!));
    }

    [Fact]
    public void A_pawn_pushed_back_to_the_start_ranks_after_one_that_never_left_it()
    {
        var (room, players) = Started(3);
        var state = State(9, "", Pawn(1, 26, 9), Pawn(2, 0, 5), Pawn(3, 0));

        var decision = Apply(room, players[0].Id, ChogRaceMatchRules.Roll, state, new Dice(1));

        Assert.Equal([(players[0].Id, 1), (players[1].Id, 3), (players[2].Id, 2)], Ranks(decision.Standings!));
    }

    [Fact]
    public void Every_player_still_playing_gets_a_result_and_leavers_get_none()
    {
        var (room, players) = Started(3);
        room.Leave(players[2].Id, Now.AddMinutes(2));
        var state = State(
            9,
            "",
            Pawn(1, 26, 9, ""","stats":{"cardsPlayed":2,"attacksLanded":1}"""),
            Pawn(2, 10, 4, ""","stats":{"cardsPlayed":1,"attacksBlocked":1}"""),
            Pawn(3, 20, 5));

        var decision = Apply(room, players[0].Id, ChogRaceMatchRules.Roll, state, new Dice(1));

        Assert.Equal([(players[0].Id, 1), (players[1].Id, 2)], Ranks(decision.Standings!));
        Assert.Equal(
            """{"tile":27,"tilesLeft":0,"finished":true,"cardsPlayed":2,"attacksLanded":1,"attacksBlocked":0}""",
            ResultOf(decision.Standings!, players[0].Id));
        Assert.Equal(
            """{"tile":10,"tilesLeft":17,"finished":false,"cardsPlayed":1,"attacksLanded":0,"attacksBlocked":1}""",
            ResultOf(decision.Standings!, players[1].Id));
    }

    /// <summary>
    /// A track whose tiles 24 and 26 both boost to the Finish, and a Card tile on 2, so one Reverse can push two pawns
    /// there and another onto a Card tile.
    /// </summary>
    private static readonly RaceRules TwoBoostsToFinish = RaceRules.Create(
        2,
        [Start(0), Normal(1), Card(2), .. Enumerable.Range(3, 21).Select(Normal), Boost(24, to: 29), Normal(25), Boost(26, to: 29), Normal(27), Normal(28), Finish(29)],
        1,
        3,
        TimeSpan.FromMinutes(20),
        [RaceCard.Reverse, RaceCard.Pump],
        3,
        RaceRulesV1.Rules.CardValues);

    [Fact]
    public void A_card_resolves_in_full_before_the_finish_is_checked_and_the_first_pushed_pawn_on_it_wins()
    {
        var rules = new ChogRaceMatchRules([RaceRulesV1.Rules, TwoBoostsToFinish]);
        var (room, players) = Started(4, rules);
        var state = """
            {"rulesVersion":2,"pawns":[{"seat":1,"tile":10,"since":1,"hand":["Reverse"]},{"seat":2,"tile":27,"since":2},{"seat":3,"tile":25,"since":3},{"seat":4,"tile":3,"since":4}],
             "deck":["Pump"],"discard":[],"moves":4,"lastMove":{"seat":1,"roll":1,"from":9,"landed":10,"to":10},"playPhase":1}
            """;

        var decision = Apply(room, players[0].Id, ChogRaceMatchRules.PlayCard, state, new NoDraws(), """{"card":"Reverse"}""", rules);

        var next = decision.State!.Value;
        Assert.Equal(MatchRuleOutcome.Accepted, decision.Outcome);
        Assert.Equal(2, next.GetProperty("winner").GetInt32());
        Assert.Equal("Finish", next.GetProperty("ended").GetString());
        // Seat 4, pushed after the winner, still landed on the Card tile and drew.
        Assert.Equal(2, PawnOf(next, 4).GetProperty("tile").GetInt32());
        Assert.Equal("""["Pump"]""", PawnOf(next, 4).GetProperty("hand").GetRawText());
        Assert.Equal(
            [(players[0].Id, 3), (players[1].Id, 1), (players[2].Id, 2), (players[3].Id, 4)],
            Ranks(decision.Standings!));
        Assert.True(decision.Standings!.Single(s => s.PlayerId == players[2].Id).Result!.Value.GetProperty("finished").GetBoolean());
    }

    [Fact]
    public void The_player_whose_turn_it_is_wins_before_any_pawn_their_move_pushed()
    {
        var rules = new ChogRaceMatchRules([RaceRulesV1.Rules, TwoBoostsToFinish]);
        var (room, players) = Started(2, rules);
        var state = """{"rulesVersion":2,"pawns":[{"seat":1,"tile":28},{"seat":2,"tile":10}],"deck":[],"discard":[]}""";

        var decision = Apply(room, players[0].Id, ChogRaceMatchRules.Roll, state, new Dice(1), rules: rules);

        Assert.Equal(1, decision.State!.Value.GetProperty("winner").GetInt32());
        Assert.Equal([(players[0].Id, 1), (players[1].Id, 2)], Ranks(decision.Standings!));
    }

    [Fact]
    public void At_the_time_cap_the_furthest_pawn_wins_and_the_rest_follow_by_tile_and_arrival()
    {
        var (room, players) = Started(4);
        var state = State(9, "", Pawn(1, 12, 9), Pawn(2, 20, 7), Pawn(3, 12, 3), Pawn(4, 0));

        var ending = EndMatch(room, state);

        Assert.Equal(
            [(players[0].Id, 3), (players[1].Id, 1), (players[2].Id, 2), (players[3].Id, 4)],
            Ranks(ending.Standings));
        Assert.Equal("TimeUp", ending.State.GetProperty("ended").GetString());
        Assert.False(ending.State.TryGetProperty("winner", out _));
        Assert.Equal(
            """{"tile":20,"tilesLeft":7,"finished":false,"cardsPlayed":0,"attacksLanded":0,"attacksBlocked":0}""",
            ResultOf(ending.Standings, players[1].Id));
    }

    [Fact]
    public void Players_level_at_the_time_cap_share_the_win()
    {
        var (room, players) = Started(3);

        var ending = EndMatch(room, State(0, "", Pawn(1, 0), Pawn(2, 0), Pawn(3, 0)));

        Assert.Equal([(players[0].Id, 1), (players[1].Id, 1), (players[2].Id, 1)], Ranks(ending.Standings));
    }

    [Fact]
    public void The_time_cap_drops_a_play_phase_and_voids_an_attack_waiting_for_its_reply()
    {
        var (room, players) = Started(2);
        var state = State(
            2,
            ""","lastMove":{"seat":1,"roll":1,"from":4,"landed":5,"to":5,"play":{"card":"Rug","target":2}},"playPhase":1,"reply":{"seat":1,"card":"Rug","target":2}""",
            Pawn(1, 5, 2, ""","stats":{"cardsPlayed":1}"""),
            Pawn(2, 9, 1, ""","hand":["Hodl"]"""));

        var ending = EndMatch(room, state);

        Assert.False(ending.State.TryGetProperty("playPhase", out _));
        Assert.False(ending.State.TryGetProperty("reply", out _));
        Assert.Equal(9, PawnOf(ending.State, 2).GetProperty("tile").GetInt32());
        Assert.Equal([(players[0].Id, 2), (players[1].Id, 1)], Ranks(ending.Standings));
    }

    [Fact]
    public void The_time_cap_ranks_only_players_still_playing()
    {
        var (room, players) = Started(3);
        room.Leave(players[1].Id, Now.AddMinutes(2));

        var ending = EndMatch(room, State(3, "", Pawn(1, 4, 1), Pawn(2, 20, 2), Pawn(3, 6, 3)));

        Assert.Equal([(players[0].Id, 2), (players[2].Id, 1)], Ranks(ending.Standings));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("""{"rulesVersion":1,"pawns":[{"seat":1,"tile":99},{"seat":2,"tile":0}]}""")]
    [InlineData("""{"rulesVersion":7,"pawns":[{"seat":1,"tile":3},{"seat":2,"tile":0}]}""")]
    [InlineData("""{"rulesVersion":1,"pawns":[{"seat":1,"tile":3,"since":4},{"seat":2,"tile":0}],"moves":2}""")]
    [InlineData("""{"rulesVersion":1,"pawns":[{"seat":1,"tile":3,"stats":{"cardsPlayed":-1}},{"seat":2,"tile":0}]}""")]
    public void A_state_this_server_cant_play_ends_with_everyone_sharing_the_rank(string? state)
    {
        var (room, players) = Started(2);

        var ending = EndMatch(room, state);

        Assert.Equal([(players[0].Id, 1), (players[1].Id, 1)], Ranks(ending.Standings));
        Assert.All(ending.Standings, standing => Assert.Null(standing.Result));
        if (state is not null)
        {
            Assert.Equal(JsonDocument.Parse(state).RootElement.GetRawText(), ending.State.GetRawText());
        }
    }

    [Fact]
    public void Playing_a_card_counts_for_its_player()
    {
        var (room, players) = Started(2);
        var state = State(1, Rolled, Pawn(1, 1, 1, ""","hand":["Pump"]"""), Pawn(2, 0));

        var decision = Apply(room, players[0].Id, ChogRaceMatchRules.PlayCard, state, new NoDraws(), """{"card":"Pump"}""");

        Assert.Equal("""{"cardsPlayed":1}""", StatsOf(decision.State!.Value, 1));
        Assert.Null(StatsOf(decision.State.Value, 2));
    }

    [Theory]
    [InlineData("Rug")]
    [InlineData("Liquidated")]
    [InlineData("Whale")]
    public void An_attack_on_a_player_without_cards_lands_at_once_and_counts_for_the_attacker(string card)
    {
        var (room, players) = Started(2);
        var state = State(1, Rolled, Pawn(1, 3, 1, $$""","hand":["{{card}}"]"""), Pawn(2, 10));

        var decision = Apply(room, players[0].Id, ChogRaceMatchRules.PlayCard, state, new NoDraws(), $$"""{"card":"{{card}}","target":2}""");

        Assert.Equal("""{"cardsPlayed":1,"attacksLanded":1}""", StatsOf(decision.State!.Value, 1));
    }

    [Fact]
    public void A_reverse_counts_as_an_attack_only_when_it_pushes_someone()
    {
        var (room, players) = Started(2);

        var pushed = Apply(
            room, players[0].Id, ChogRaceMatchRules.PlayCard, State(1, Rolled, Pawn(1, 3, 1, ""","hand":["Reverse"]"""), Pawn(2, 10)), new NoDraws(), """{"card":"Reverse"}""");
        var nobody = Apply(
            room, players[0].Id, ChogRaceMatchRules.PlayCard, State(1, Rolled, Pawn(1, 3, 1, ""","hand":["Reverse"]"""), Pawn(2, 0)), new NoDraws(), """{"card":"Reverse"}""");

        Assert.Equal("""{"cardsPlayed":1,"attacksLanded":1}""", StatsOf(pushed.State!.Value, 1));
        Assert.Equal("""{"cardsPlayed":1}""", StatsOf(nobody.State!.Value, 1));
    }

    [Fact]
    public void A_shield_blocking_an_attack_counts_for_its_target()
    {
        var (room, players) = Started(2);
        var state = State(1, Rolled, Pawn(1, 3, 1, ""","hand":["Rug"]"""), Pawn(2, 10, null, ""","hand":["Pump"],"shielded":true"""));

        var decision = Apply(room, players[0].Id, ChogRaceMatchRules.PlayCard, state, new NoDraws(), """{"card":"Rug","target":2}""");

        Assert.Equal("""{"cardsPlayed":1}""", StatsOf(decision.State!.Value, 1));
        Assert.Equal("""{"attacksBlocked":1}""", StatsOf(decision.State.Value, 2));
        Assert.False(PawnOf(decision.State.Value, 2).TryGetProperty("shielded", out _));
    }

    /// <summary>Seat 1's Rug on seat 2 waits for seat 2's reply, the engine waiting with it.</summary>
    private static string WaitingRug(GameRoom room, Player[] players)
    {
        using var payload = JsonDocument.Parse("{}");
        room.ApplyCommand(
            players[0].Id,
            new MatchCommand(Guid.NewGuid(), room.Id, room.Version, ChogRaceMatchRules.PlayCard, payload.RootElement.Clone()),
            "{}",
            endsTurn: false,
            Now.AddMinutes(1),
            awaitsReplyFrom: players[1].Id);
        return State(
            1,
            ""","lastMove":{"seat":1,"roll":1,"from":4,"landed":5,"to":5,"play":{"card":"Rug","target":2}},"reply":{"seat":1,"card":"Rug","target":2}""",
            Pawn(1, 5, 1, ""","stats":{"cardsPlayed":1}"""),
            Pawn(2, 12, null, ""","hand":["Hodl"]"""));
    }

    [Fact]
    public void Hodl_in_reply_counts_as_a_card_played_and_an_attack_blocked()
    {
        var (room, players) = Started(2);
        var state = WaitingRug(room, players);

        var decision = Apply(room, players[1].Id, ChogRaceMatchRules.PlayCard, state, new NoDraws(), """{"card":"Hodl"}""");

        Assert.Equal("""{"cardsPlayed":1}""", StatsOf(decision.State!.Value, 1));
        Assert.Equal("""{"cardsPlayed":1,"attacksBlocked":1}""", StatsOf(decision.State.Value, 2));
        Assert.Equal(1, decision.State.Value.GetProperty("moves").GetInt32());
    }

    [Fact]
    public void An_attack_let_through_lands_counts_for_the_attacker_and_stamps_the_pushed_pawn()
    {
        var (room, players) = Started(2);
        var state = WaitingRug(room, players);

        var decision = Apply(room, players[1].Id, ChogRaceMatchRules.Pass, state, new NoDraws());

        Assert.Equal("""{"cardsPlayed":1,"attacksLanded":1}""", StatsOf(decision.State!.Value, 1));
        Assert.Null(StatsOf(decision.State.Value, 2));
        Assert.Equal(2, PawnOf(decision.State.Value, 2).GetProperty("since").GetInt32());
    }

    [Fact]
    public void An_attack_whose_reply_runs_out_counts_for_the_attacker()
    {
        var (room, players) = Started(2);
        var state = WaitingRug(room, players);
        using var document = JsonDocument.Parse(state);

        var decision = Rules.TimeOutReply(new MatchTurnContext(room, document.RootElement, new NoDraws()))!;

        Assert.Equal("""{"cardsPlayed":1,"attacksLanded":1}""", StatsOf(decision.State!.Value, 1));
    }

    [Fact]
    public void The_board_shows_how_the_race_ended_but_never_statistics_or_arrival_order()
    {
        var (room, players) = Started(2);
        var ending = EndMatch(room, State(3, "", Pawn(1, 12, 3, ""","stats":{"cardsPlayed":2}"""), Pawn(2, 4, 1)));
        room.End(ending.State.GetRawText(), ending.Standings, Now.AddMinutes(21));

        using var document = JsonDocument.Parse(room.State!);
        var view = Rules.ViewFor(new MatchViewContext(room, players[0].Id, document.RootElement))!.Value;

        Assert.Equal("TimeUp", view.GetProperty("ended").GetString());
        Assert.DoesNotContain("stats", view.GetRawText());
        Assert.DoesNotContain("since", view.GetRawText());
        Assert.DoesNotContain("moves", view.GetRawText());
    }
}
