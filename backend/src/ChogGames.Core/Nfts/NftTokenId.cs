using System.Globalization;
using System.Numerics;

namespace ChogGames.Core.Nfts;

/// <summary>
/// An ERC-721 token id (uint256) in its one canonical form: plain decimal digits without sign, spaces or
/// leading zeros, so the same token can never be spelled two ways.
/// </summary>
public readonly record struct NftTokenId : IComparable<NftTokenId>
{
    /// <summary>Digits in 2^256 - 1.</summary>
    public const int MaxDigits = 78;

    private static readonly BigInteger MaxValue = (BigInteger.One << 256) - 1;

    private NftTokenId(BigInteger value) => Value = value;

    public BigInteger Value { get; }

    public static bool TryParse(string? input, out NftTokenId tokenId)
    {
        tokenId = default;
        if (string.IsNullOrEmpty(input) || input.Length > MaxDigits || !input.All(char.IsAsciiDigit)
            || (input.Length > 1 && input[0] == '0'))
        {
            return false;
        }

        var value = BigInteger.Parse(input, NumberStyles.None, CultureInfo.InvariantCulture);
        if (value > MaxValue)
        {
            return false;
        }

        tokenId = new NftTokenId(value);
        return true;
    }

    public static NftTokenId From(ulong value) => new(value);

    public int CompareTo(NftTokenId other) => Value.CompareTo(other.Value);

    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
}
