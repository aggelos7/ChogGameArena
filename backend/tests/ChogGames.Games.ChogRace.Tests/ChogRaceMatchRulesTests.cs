using System.Text.Json;
using ChogGames.Core.Games;
using ChogGames.Core.Matches;
using ChogGames.Core.Players;
using ChogGames.Core.Rooms;
using ChogGames.Games.ChogRace.Rules;
using static ChogGames.Games.ChogRace.Rules.RaceTile;

namespace ChogGames.Games.ChogRace.Tests;

/// <summary>
/// STORY-050, DEC-054: a Chog Race match starts with every pawn on the start tile under the current rules version, and
/// every player sees that version's track and the pawns of the players still playing. There are no moves yet.
/// </summary>
public class ChogRaceMatchRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    private static readonly ChogRaceMatchRules Rules = new([RaceRulesV1.Rules]);

    /// <summary>Never reorders a shuffle, so seats follow join order (host first).</summary>
    private sealed class KeepOrder : IRandomSource
    {
        public int Next(int minInclusive, int maxExclusive) => maxExclusive - 1;
    }

    /// <summary>Fails the test if the rules draw anything.</summary>
    private sealed class NoDraws : IRandomSource
    {
        public int Next(int minInclusive, int maxExclusive) => throw new InvalidOperationException("Nothing should be drawn.");
    }

    private static Player Guest(int n)
    {
        Assert.True(PlayerDisplayName.TryCreate($"Player {n}", out var name));
        return Player.CreateGuest(name, Now);
    }

    /// <summary>A started Chog Race match of <paramref name="count"/> players, with the rules' start state stored.</summary>
    private static (GameRoom Room, Player[] Players) Started(int count, ChogRaceMatchRules? rules = null)
    {
        rules ??= Rules;
        var players = Enumerable.Range(1, count).Select(Guest).ToArray();
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
            startState: started => rules.StartState(new MatchStartContext(started, random))?.GetRawText());
        return (room, players);
    }

    private static JsonElement? ViewOf(GameRoom room, Guid viewer, string state, ChogRaceMatchRules? rules = null)
    {
        using var document = JsonDocument.Parse(state);
        return (rules ?? Rules).ViewFor(new MatchViewContext(room, viewer, document.RootElement))?.Clone();
    }

    private static JsonElement View(GameRoom room, Guid viewer, ChogRaceMatchRules? rules = null) =>
        ViewOf(room, viewer, room.State!, rules) ?? throw new Xunit.Sdk.XunitException("No view.");

    private static (int Seat, int Tile)[] Pawns(JsonElement view) =>
        view.GetProperty("pawns").EnumerateArray()
            .Select(pawn => (pawn.GetProperty("seat").GetInt32(), pawn.GetProperty("tile").GetInt32()))
            .ToArray();

    [Theory]
    [InlineData(2)]
    [InlineData(6)]
    public void Every_player_starts_on_the_start_tile_under_the_current_version(int count)
    {
        var (room, _) = Started(count);

        using var state = JsonDocument.Parse(room.State!);
        Assert.Equal(1, state.RootElement.GetProperty("rulesVersion").GetInt32());
        Assert.Equal(Enumerable.Range(1, count).Select(seat => (seat, 0)), Pawns(state.RootElement));
        Assert.Equal(GameDefinition.ChogRace, Rules.GameType);
        Assert.Null(((IMatchRules)Rules).TimeLimit);
    }

    [Fact]
    public void Every_player_sees_the_same_track_and_all_six_pawns()
    {
        var (room, players) = Started(6);

        Assert.Single(players.Select(p => View(room, p.Id).GetRawText()).Distinct());
        var view = View(room, players[0].Id);
        Assert.Equal(1, view.GetProperty("rulesVersion").GetInt32());
        Assert.Equal([(1, 0), (2, 0), (3, 0), (4, 0), (5, 0), (6, 0)], Pawns(view));
    }

    [Fact]
    public void The_track_shows_every_tile_with_its_type_and_jump_target()
    {
        var (room, players) = Started(2);

        var tiles = View(room, players[0].Id).GetProperty("tiles").EnumerateArray().Select(t => t.GetRawText()).ToArray();

        Assert.Equal(28, tiles.Length);
        Assert.Equal("""{"index":0,"type":"Start"}""", tiles[0]);
        Assert.Equal("""{"index":2,"type":"Card"}""", tiles[2]);
        Assert.Equal("""{"index":4,"type":"Boost","to":6}""", tiles[4]);
        Assert.Equal("""{"index":9,"type":"Setback","to":7}""", tiles[9]);
        Assert.Equal("""{"index":27,"type":"Finish"}""", tiles[27]);
    }

    [Fact]
    public void A_player_who_left_is_taken_off_the_track()
    {
        var (room, players) = Started(3);

        room.Leave(players[1].Id, Now.AddMinutes(2));

        Assert.Equal([(1, 0), (3, 0)], Pawns(View(room, players[0].Id)));
        Assert.Equal([(1, 0), (3, 0)], Pawns(View(room, players[1].Id)));
    }

    [Fact]
    public void A_match_keeps_the_track_of_the_version_it_started_with()
    {
        var (room, players) = Started(2);
        var version2 = RaceRules.Create(
            2,
            [Start(0), .. Enumerable.Range(1, 28).Select(Normal), Finish(29)],
            1,
            3,
            TimeSpan.FromMinutes(20));
        var both = new ChogRaceMatchRules([RaceRulesV1.Rules, version2]);

        Assert.Equal(28, View(room, players[0].Id, both).GetProperty("tiles").GetArrayLength());

        var (newer, newPlayers) = Started(2, both);
        var view = View(newer, newPlayers[0].Id, both);
        Assert.Equal((2, 30), (view.GetProperty("rulesVersion").GetInt32(), view.GetProperty("tiles").GetArrayLength()));
    }

    [Theory]
    [InlineData("""{"rulesVersion":9,"pawns":[]}""")]
    [InlineData("""{"rulesVersion":1}""")]
    [InlineData("""{"rulesVersion":1,"pawns":[null]}""")]
    [InlineData("""{"rulesVersion":"one","pawns":[]}""")]
    [InlineData("""[]""")]
    public void A_state_the_server_cant_read_shows_nothing(string state)
    {
        var (room, players) = Started(2);

        Assert.Null(ViewOf(room, players[0].Id, state));
    }

    [Theory]
    [InlineData("ROLL")]
    [InlineData("PLAY_CARD")]
    [InlineData("END_TURN")]
    public void There_are_no_moves_yet_and_a_turn_that_runs_out_is_skipped(string type)
    {
        var (room, players) = Started(2);
        using var state = JsonDocument.Parse(room.State!);
        using var payload = JsonDocument.Parse("{}");

        var decision = Rules.Apply(new MatchCommandContext(room, players[0].Id, type, payload.RootElement, state.RootElement, new NoDraws()));

        Assert.Equal(MatchRuleOutcome.UnknownCommand, decision.Outcome);
        Assert.Null(Rules.TimeOutTurn(new MatchTurnContext(room, state.RootElement, new NoDraws())));
    }

    [Fact]
    public void The_rules_need_at_least_one_version()
    {
        Assert.Throws<ArgumentException>(() => new ChogRaceMatchRules([]));
        Assert.Throws<ArgumentNullException>(() => new ChogRaceMatchRules(null!));
    }
}
