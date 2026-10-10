using ChogGames.Core.Nfts;

namespace ChogGames.Core.Tests.Nfts;

public class NftTraitTests
{
    [Fact]
    public void Unknown_types_and_values_are_kept_trimmed()
    {
        var trait = new NftTrait("  Mystery Aura ", " Shimmering ✨ ");

        Assert.Equal("Mystery Aura", trait.Type);
        Assert.Equal("Shimmering ✨", trait.Value);
    }

    [Theory]
    [InlineData("", "Value")]
    [InlineData("   ", "Value")]
    [InlineData("Type", "")]
    [InlineData("Ty\npe", "Value")]
    [InlineData("Type", "Va\u0000lue")]
    public void Empty_or_control_characters_are_rejected(string type, string value)
    {
        Assert.Throws<ArgumentException>(() => new NftTrait(type, value));
    }

    [Fact]
    public void Length_is_bounded()
    {
        Assert.Equal(NftTrait.MaxLength, new NftTrait("Lore", new string('x', NftTrait.MaxLength)).Value.Length);
        Assert.Throws<ArgumentException>(() => new NftTrait("Lore", new string('x', NftTrait.MaxLength + 1)));
    }

    [Fact]
    public void Null_is_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => new NftTrait(null!, "Value"));
    }
}
