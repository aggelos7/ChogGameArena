namespace ChogGames.Core.Wallets;

/// <summary>
/// A 20-byte EVM account address, normalized to lowercase <c>0x</c>-prefixed hex for storage and comparison.
/// Holding one proves nothing about ownership; only a verified signature does (ADR-005).
/// </summary>
public readonly record struct WalletAddress
{
    public const int Length = 42;

    private WalletAddress(string value) => Value = value;

    public string Value { get; }

    public static bool TryParse(string? input, out WalletAddress address)
    {
        address = default;
        if (input is null || input.Length != Length || input[0] != '0' || input[1] is not ('x' or 'X'))
        {
            return false;
        }

        for (var i = 2; i < Length; i++)
        {
            if (!char.IsAsciiHexDigit(input[i]))
            {
                return false;
            }
        }

        address = new WalletAddress("0x" + input[2..].ToLowerInvariant());
        return true;
    }

    /// <summary>From the 20 address bytes (e.g. the tail of a recovered public key hash).</summary>
    public static WalletAddress FromBytes(ReadOnlySpan<byte> bytes) =>
        bytes.Length == 20
            ? new WalletAddress("0x" + Convert.ToHexString(bytes).ToLowerInvariant())
            : throw new ArgumentException("An address is exactly 20 bytes.", nameof(bytes));

    public override string ToString() => Value;
}
