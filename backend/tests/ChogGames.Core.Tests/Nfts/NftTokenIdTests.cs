using System.Numerics;
using ChogGames.Core.Nfts;

namespace ChogGames.Core.Tests.Nfts;

public class NftTokenIdTests
{
    private const string MaxUint256 = "115792089237316195423570985008687907853269984665640564039457584007913129639935";

    [Theory]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("1969")]
    [InlineData(MaxUint256)]
    public void Canonical_decimal_ids_round_trip(string input)
    {
        Assert.True(NftTokenId.TryParse(input, out var tokenId));
        Assert.Equal(input, tokenId.ToString());
        Assert.Equal(BigInteger.Parse(input), tokenId.Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" 1")]
    [InlineData("1 ")]
    [InlineData("+1")]
    [InlineData("-1")]
    [InlineData("01")]
    [InlineData("1.0")]
    [InlineData("1e3")]
    [InlineData("0x10")]
    [InlineData("١٢")]
    [InlineData("115792089237316195423570985008687907853269984665640564039457584007913129639936")]
    [InlineData("1000000000000000000000000000000000000000000000000000000000000000000000000000000")]
    public void Non_canonical_or_out_of_range_input_is_rejected(string? input)
    {
        Assert.False(NftTokenId.TryParse(input, out var tokenId));
        Assert.Equal(default, tokenId);
    }

    [Fact]
    public void Equal_ids_are_equal_and_order_numerically()
    {
        Assert.True(NftTokenId.TryParse("10", out var ten));
        Assert.Equal(NftTokenId.From(10), ten);
        Assert.True(NftTokenId.From(9).CompareTo(ten) < 0);
    }
}
