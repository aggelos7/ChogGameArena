using ChogGames.Core.Rooms;

namespace ChogGames.Core.Tests.Rooms;

public class RoomCodeTests
{
    [Fact]
    public void Alphabet_has_31_characters_without_look_alikes()
    {
        Assert.Equal(31, RoomCode.Alphabet.Distinct().Count());
        Assert.DoesNotContain(RoomCode.Alphabet, c => "01OIL".Contains(c));
    }

    [Theory]
    [InlineData("K7QF3M", "K7QF3M")]
    [InlineData("k7qf3m", "K7QF3M")]
    [InlineData("  K7qF3m ", "K7QF3M")]
    [InlineData("222222", "222222")]
    [InlineData("ZZZZZZ", "ZZZZZZ")]
    public void Valid_codes_are_normalized_to_upper_case(string input, string expected)
    {
        Assert.True(RoomCode.TryParse(input, out var code));
        Assert.Equal(expected, code.Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("K7QF3")]
    [InlineData("K7QF3MX")]
    [InlineData("K7QF0M")]
    [InlineData("K7QFOM")]
    [InlineData("K7QF1M")]
    [InlineData("K7QFIM")]
    [InlineData("K7QFLM")]
    [InlineData("K7Q F3")]
    [InlineData("K7QF3-")]
    [InlineData("K7QF3Ä")]
    public void Malformed_codes_are_rejected(string? input)
    {
        Assert.False(RoomCode.TryParse(input, out _));
    }
}
