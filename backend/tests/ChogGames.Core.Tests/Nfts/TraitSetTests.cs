using ChogGames.Core.Nfts;
using ChogGames.Core.Players;
using ChogGames.Core.Wallets;

namespace ChogGames.Core.Tests.Nfts;

/// <summary>
/// STORY-032, DEC-034: normalized traits for a future resolver. Case and whitespace don't matter, duplicates collapse,
/// the order is fixed, and the display text of the traits is left alone.
/// </summary>
public class TraitSetTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

    private static CharacterSnapshot NftChog(params NftTrait[] traits)
    {
        Assert.True(WalletAddress.TryParse("0x" + new string('a', 40), out var wallet));
        return CharacterSnapshot.Nft(wallet, new OwnedNft(NftTokenId.From(5), "Chog #5", null, traits), Now);
    }

    [Theory]
    [InlineData("Rarity", "Legendary")]
    [InlineData("rarity", "legendary")]
    [InlineData("RARITY", "LEGENDARY")]
    [InlineData("  Rarity ", "\tLegendary\n")]
    public void Case_and_outer_whitespace_do_not_matter(string type, string value)
    {
        var traits = TraitSet.From([new NftTrait(type, value)]);

        Assert.Equal([new NormalizedTrait("rarity", "legendary")], traits.Traits);
        Assert.True(traits.Has("Rarity", "Legendary"));
        Assert.True(traits.Has(" rarity", "LEGENDARY "));
    }

    [Fact]
    public void Inner_whitespace_collapses_to_one_space()
    {
        var traits = TraitSet.From([new NftTrait("Eye   Color", "Deep \u00A0 Blue")]);

        Assert.Equal([new NormalizedTrait("eye color", "deep blue")], traits.Traits);
        Assert.True(traits.Has("eye color", "deep blue"));
        Assert.False(traits.Has("eyecolor", "deep blue"));
    }

    [Fact]
    public void Duplicates_collapse_and_the_order_is_fixed()
    {
        var a = TraitSet.From([new NftTrait("Hat", "Cap"), new NftTrait("Background", "Gold"), new NftTrait("hat", "CAP")]);
        var b = TraitSet.From([new NftTrait("background", "gold"), new NftTrait("Hat", "Cap")]);

        Assert.Equal([new NormalizedTrait("background", "gold"), new NormalizedTrait("hat", "cap")], a.Traits);
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void A_type_may_have_several_values()
    {
        var traits = TraitSet.From([new NftTrait("Accessory", "Scarf"), new NftTrait("Accessory", "Bell"), new NftTrait("Hat", "Cap")]);

        Assert.Equal(["bell", "scarf"], traits.ValuesOf("ACCESSORY"));
        Assert.Empty(traits.ValuesOf("Aura"));
        Assert.Equal(3, traits.Traits.Count);
    }

    [Fact]
    public void Unicode_text_is_kept_lower_cased_with_the_invariant_culture()
    {
        var traits = TraitSet.From([new NftTrait("Élément", "ÉCLAIR"), new NftTrait("Title", "INFO")]);

        Assert.True(traits.Has("élément", "éclair"));
        Assert.Equal(["info"], traits.ValuesOf("title"));
    }

    [Fact]
    public void Standard_chogs_and_traitless_nfts_have_no_traits()
    {
        Assert.Same(TraitSet.Empty, TraitSet.From(CharacterSnapshot.Standard(DefaultAvatar.Classic, Now)));
        Assert.Same(TraitSet.Empty, TraitSet.From(NftChog()));
        Assert.Empty(TraitSet.Empty.Traits);
    }

    [Fact]
    public void A_snapshot_gives_its_traits_and_keeps_their_display_text()
    {
        var character = NftChog(new NftTrait("Rarity", "Legendary"), new NftTrait("Eye  Color", "Blue"));

        var traits = TraitSet.From(character);

        Assert.Equal([new NormalizedTrait("eye color", "blue"), new NormalizedTrait("rarity", "legendary")], traits.Traits);
        Assert.Equal([new NftTrait("Rarity", "Legendary"), new NftTrait("Eye  Color", "Blue")], character.Traits);
    }

    [Fact]
    public void Missing_input_is_refused()
    {
        Assert.Throws<ArgumentNullException>(() => TraitSet.From((CharacterSnapshot)null!));
        Assert.Throws<ArgumentNullException>(() => TraitSet.From((IEnumerable<NftTrait>)null!));
        Assert.Throws<ArgumentException>(() => TraitSet.From([new NftTrait("Hat", "Cap"), null!]));
        Assert.Throws<ArgumentNullException>(() => TraitSet.Empty.Has(null!, "x"));
        Assert.Throws<ArgumentNullException>(() => TraitSet.Empty.ValuesOf(null!));
    }
}
