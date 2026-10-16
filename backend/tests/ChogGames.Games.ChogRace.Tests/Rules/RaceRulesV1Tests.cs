using ChogGames.Games.ChogRace.Rules;

namespace ChogGames.Games.ChogRace.Tests.Rules;

/// <summary>
/// STORY-050, DEC-054: the shipped rules have the agreed values and a track every pawn can finish. STORY-052, DEC-056:
/// a deck of three of each card and hands of up to three. STORY-053, DEC-057: the card values.
/// </summary>
public class RaceRulesV1Tests
{
    private static readonly RaceRules Rules = RaceRulesV1.Rules;

    [Fact]
    public void Version_1_has_the_agreed_values()
    {
        Assert.Equal(("chog-race", 1), (Rules.GameType, Rules.Version));
        Assert.Equal(28, Rules.Tiles.Count);
        Assert.Equal((1, 3), (Rules.MinMove, Rules.MaxMove));
        Assert.Equal(TimeSpan.FromMinutes(20), Rules.TimeCap);
        Assert.Equal((RaceTileType.Start, RaceTileType.Finish), (Rules.Start.Type, Rules.Finish.Type));
    }

    [Fact]
    public void The_deck_has_three_of_each_card_and_hands_hold_three()
    {
        Assert.Equal(24, Rules.Deck.Count);
        Assert.All(Enum.GetValues<RaceCard>(), card => Assert.Equal(3, Rules.Deck.Count(c => c == card)));
        Assert.Equal(8, Enum.GetValues<RaceCard>().Length);
        Assert.Equal(3, Rules.MaxHand);
    }

    [Fact]
    public void Pump_moves_3_fomo_5_forward_or_3_back_at_even_odds_and_reverse_1_back() =>
        Assert.Equal(new RaceCardValues(Pump: 3, FomoForward: 5, FomoBack: 3, FomoForwardChance: 50, Reverse: 1), Rules.CardValues);

    [Fact]
    public void The_track_has_seven_card_tiles_three_boosts_and_three_setbacks()
    {
        int[] Of(RaceTileType type) => [.. Rules.Tiles.Where(tile => tile.Type == type).Select(tile => tile.Index)];

        Assert.Equal([2, 7, 11, 14, 18, 22, 25], Of(RaceTileType.Card));
        Assert.Equal([4, 12, 20], Of(RaceTileType.Boost));
        Assert.Equal([9, 16, 23], Of(RaceTileType.Setback));
        Assert.Equal([6, 15, 22], Of(RaceTileType.Boost).Select(i => Rules.Tiles[i].To!.Value));
        Assert.Equal([7, 13, 21], Of(RaceTileType.Setback).Select(i => Rules.Tiles[i].To!.Value));
    }

    [Fact]
    public void Without_cards_a_pawn_finishes_in_about_fifteen_and_a_half_rolls()
    {
        // Expected rolls to the finish from each tile, with every move of 1 to 3 equally likely, iterated to a fixed point.
        var finish = Rules.Finish.Index;
        var expected = new double[Rules.Tiles.Count];
        for (var round = 0; round < 1_000; round++)
        {
            for (var tile = finish - 1; tile >= 0; tile--)
            {
                expected[tile] = 1 + Enumerable.Range(Rules.MinMove, Rules.MaxMove - Rules.MinMove + 1)
                    .Select(steps => Math.Min(tile + steps, finish))
                    .Average(to => expected[Rules.Tiles[to].To ?? to]);
            }
        }

        Assert.InRange(expected[0], 15, 16);
    }
}
