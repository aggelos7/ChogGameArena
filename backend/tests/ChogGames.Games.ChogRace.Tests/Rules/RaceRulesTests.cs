using ChogGames.Games.ChogRace.Rules;
using static ChogGames.Games.ChogRace.Rules.RaceTile;

namespace ChogGames.Games.ChogRace.Tests.Rules;

/// <summary>
/// STORY-050, DEC-054: the Chog Race rules are checked when built. A track whose finish a pawn can't reach, a jump off
/// the track or into another jump, and out-of-range moves or caps are refused, naming the problem. STORY-052, DEC-056:
/// so are an empty deck, unknown cards and a hand limit out of range.
/// </summary>
public class RaceRulesTests
{
    private static readonly TimeSpan Cap = TimeSpan.FromMinutes(20);

    private static readonly RaceCard[] Deck = [RaceCard.Pump, RaceCard.Hodl];

    /// <summary>A valid track of <paramref name="count"/> tiles: Start, Normal tiles and the Finish.</summary>
    private static RaceTile[] Plain(int count = 24) =>
        [Start(0), .. Enumerable.Range(1, count - 2).Select(Normal), Finish(count - 1)];

    private static RaceTile[] With(params RaceTile[] changes)
    {
        var track = Plain();
        foreach (var tile in changes)
        {
            track[tile.Index] = tile;
        }

        return track;
    }

    private static RaceRules Create(RaceTile[] track, int minMove = 1, int maxMove = 3) =>
        RaceRules.Create(1, track, minMove, maxMove, Cap, Deck, 3);

    private static string Refused(RaceTile[] track, int minMove = 1, int maxMove = 3) =>
        Assert.ThrowsAny<ArgumentException>(() => Create(track, minMove, maxMove)).Message;

    [Theory]
    [InlineData(24)]
    [InlineData(30)]
    public void A_track_of_24_to_30_tiles_is_accepted(int count)
    {
        var rules = Create(Plain(count));

        Assert.Equal(count, rules.Tiles.Count);
        Assert.Equal((RaceTileType.Start, RaceTileType.Finish), (rules.Start.Type, rules.Finish.Type));
        Assert.Equal(("chog-race", 1, 3), (rules.GameType, rules.MinMove, rules.MaxMove));
    }

    [Theory]
    [InlineData(23)]
    [InlineData(31)]
    public void A_track_of_the_wrong_length_is_refused(int count) =>
        Assert.StartsWith($"The track has {count} tiles; it needs 24 to 30.", Refused(Plain(count)));

    [Fact]
    public void Tiles_must_be_in_index_order_with_one_start_and_one_finish()
    {
        var misnumbered = Plain();
        misnumbered[3] = Normal(4);
        Assert.StartsWith("Tile 3 has index 4", Refused(misnumbered));
        Assert.StartsWith("Tile 0 is Normal", Refused(With(Normal(0))));
        Assert.StartsWith("Tile 23 is Card", Refused(With(Card(23))));
        Assert.StartsWith("Tile 5 is Finish", Refused(With(Finish(5))));
        Assert.StartsWith("Tile 5 is Start", Refused(With(Start(5))));
        Assert.StartsWith("Tile 5 has an unknown type", Refused(With(new RaceTile(5, (RaceTileType)99))));

        var missing = Plain();
        missing[7] = null!;
        Assert.StartsWith("Tile 7 is missing", Refused(missing));
    }

    [Fact]
    public void Boosts_jump_forward_and_setbacks_back_by_at_most_six_tiles()
    {
        var rules = Create(With(Boost(4, to: 10), Setback(16, to: 10)));
        Assert.Equal((10, 10), (rules.Tiles[4].To, rules.Tiles[16].To));

        Assert.StartsWith("Tile 4 is a Boost to tile 2; a Boost jumps forward", Refused(With(Boost(4, to: 2))));
        Assert.StartsWith("Tile 4 is a Setback to tile 6;", Refused(With(Setback(4, to: 6))));
        Assert.StartsWith("Tile 4 is a Boost to tile 4;", Refused(With(Boost(4, to: 4))));
        Assert.StartsWith("Tile 4 sends a pawn 7 tiles; a jump is at most 6.", Refused(With(Boost(4, to: 11))));
        Assert.StartsWith("Tile 12 sends a pawn 7 tiles", Refused(With(Setback(12, to: 5))));
    }

    [Fact]
    public void A_jump_must_land_on_the_track_on_a_tile_that_doesnt_jump()
    {
        Assert.StartsWith("Tile 20 (Boost) must send a pawn to a tile on the track.", Refused(With(Boost(20, to: 24))));
        Assert.StartsWith("Tile 2 (Setback) must send a pawn to a tile on the track.", Refused(With(Setback(2, to: -1))));
        Assert.StartsWith("Tile 4 (Boost) must send a pawn to a tile on the track.", Refused(With(new RaceTile(4, RaceTileType.Boost))));
        Assert.StartsWith("Tile 4 sends a pawn to tile 6, which jumps again", Refused(With(Boost(4, to: 6), Boost(6, to: 8))));
        Assert.StartsWith("Tile 5 (Normal) sends a pawn nowhere.", Refused(With(new RaceTile(5, RaceTileType.Normal, 7))));

        // A boost may land on the finish, and a setback on the start.
        var rules = Create(With(Boost(20, to: 23), Setback(3, to: 0)));
        Assert.Equal((23, 0), (rules.Tiles[20].To, rules.Tiles[3].To));
    }

    [Fact]
    public void A_track_whose_finish_cant_be_reached_is_refused()
    {
        // Every roll from tiles 8 to 10 (1-3) lands on one of them again, and every pawn ends up there.
        var trap = With(Setback(11, to: 10), Setback(12, to: 9), Setback(13, to: 8));

        Assert.StartsWith("The finish can't be reached from tile 0 with moves of 1 to 3.", Refused(trap));
    }

    [Fact]
    public void A_trap_is_only_a_trap_for_the_moves_the_rules_allow()
    {
        var walls = With(Setback(11, to: 10), Setback(12, to: 9), Setback(13, to: 8));

        // A roll of 4 clears the three setbacks.
        Assert.Equal(4, Create(walls, maxMove: 4).MaxMove);

        // With moves of exactly 2, one setback from 4 to 2 sends every pawn round forever; with 1 to 2 it doesn't.
        var loop = With(Setback(4, to: 2));
        Assert.StartsWith("The finish can't be reached from tile 0 with moves of 2 to 2.", Refused(loop, minMove: 2, maxMove: 2));
        Assert.Equal(2, Create(loop, minMove: 1, maxMove: 2).MaxMove);
    }

    [Fact]
    public void A_tile_no_pawn_can_stand_on_doesnt_need_a_route()
    {
        // Moves of exactly 2 never stand on odd tiles, so a trap reached only from them is no trap.
        var track = With(Setback(9, to: 7), Setback(11, to: 7));

        Assert.Equal(2, Create(track, minMove: 2, maxMove: 2).MinMove);
    }

    [Theory]
    [InlineData(0, 3)]
    [InlineData(3, 2)]
    [InlineData(1, 7)]
    public void Moves_must_be_1_to_6_spaces(int minMove, int maxMove) =>
        Assert.Contains("A move is 1 to 6 spaces", Refused(Plain(), minMove, maxMove));

    [Fact]
    public void The_version_and_time_cap_are_checked()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => RaceRules.Create(0, Plain(), 1, 3, Cap, Deck, 3));
        Assert.Throws<ArgumentOutOfRangeException>(() => RaceRules.Create(1, Plain(), 1, 3, TimeSpan.Zero, Deck, 3));
        Assert.Throws<ArgumentOutOfRangeException>(() => RaceRules.Create(1, Plain(), 1, 3, TimeSpan.FromMinutes(21), Deck, 3));
        Assert.Throws<ArgumentNullException>(() => RaceRules.Create(1, null!, 1, 3, Cap, Deck, 3));
        Assert.Equal(Cap, RaceRules.Create(1, Plain(), 1, 3, Cap, Deck, 3).TimeCap);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    public void The_deck_and_hand_limit_are_kept(int maxHand)
    {
        var rules = RaceRules.Create(1, Plain(), 1, 3, Cap, [RaceCard.Pump, RaceCard.Pump, RaceCard.Rug], maxHand);

        Assert.Equal([RaceCard.Pump, RaceCard.Pump, RaceCard.Rug], rules.Deck);
        Assert.Equal(maxHand, rules.MaxHand);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    public void A_hand_holds_1_to_5_cards(int maxHand) =>
        Assert.Contains(
            "A hand holds 1 to 5 cards",
            Assert.Throws<ArgumentOutOfRangeException>(() => RaceRules.Create(1, Plain(), 1, 3, Cap, Deck, maxHand)).Message);

    [Fact]
    public void The_deck_needs_cards_of_known_kinds()
    {
        Assert.Contains(
            "The deck needs at least one card",
            Assert.Throws<ArgumentException>(() => RaceRules.Create(1, Plain(), 1, 3, Cap, [], 3)).Message);
        Assert.Contains(
            "The deck needs at least one card",
            Assert.Throws<ArgumentException>(() => RaceRules.Create(1, Plain(), 1, 3, Cap, [RaceCard.Pump, (RaceCard)99], 3)).Message);
        Assert.Throws<ArgumentNullException>(() => RaceRules.Create(1, Plain(), 1, 3, Cap, null!, 3));
    }
}
