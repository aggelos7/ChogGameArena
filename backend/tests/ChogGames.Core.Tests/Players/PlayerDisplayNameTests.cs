using ChogGames.Core.Players;

namespace ChogGames.Core.Tests.Players;

public class PlayerDisplayNameTests
{
    [Theory]
    [InlineData("Bob", "Bob")]
    [InlineData("Chog 4821", "Chog 4821")]
    [InlineData("  Speedy_Chog-1  ", "Speedy_Chog-1")]
    [InlineData("Χόγκ Ελλάδα", "Χόγκ Ελλάδα")]
    [InlineData("abcdefghijklmnopqrst", "abcdefghijklmnopqrst")]
    public void Valid_names_are_accepted_and_trimmed(string input, string expected)
    {
        Assert.True(PlayerDisplayName.TryCreate(input, out var name));
        Assert.Equal(expected, name.Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ab")]
    [InlineData("abcdefghijklmnopqrstu")]
    [InlineData("two  spaces")]
    [InlineData("<script>")]
    [InlineData("name\twith tab")]
    [InlineData("emoji 🐸")]
    [InlineData("dot.name")]
    public void Invalid_names_are_rejected(string? input)
    {
        Assert.False(PlayerDisplayName.TryCreate(input, out var name));
        Assert.Null(name);
    }

    [Fact]
    public void Generated_guest_names_are_valid()
    {
        for (var i = 0; i < 100; i++)
        {
            var generated = PlayerDisplayName.GenerateGuestName();
            Assert.Matches("^Chog [0-9]{4}$", generated.Value);
            Assert.True(PlayerDisplayName.TryCreate(generated.Value, out _));
        }
    }
}
