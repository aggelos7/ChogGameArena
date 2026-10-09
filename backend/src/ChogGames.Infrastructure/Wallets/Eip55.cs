using ChogGames.Core.Wallets;

namespace ChogGames.Infrastructure.Wallets;

/// <summary>EIP-55 mixed-case checksum encoding, as wallets display addresses and EIP-4361 requires.</summary>
public static class Eip55
{
    public static string ToChecksumAddress(WalletAddress address)
    {
        var hex = address.Value[2..];
        var hash = Convert.ToHexString(Keccak256.Hash(System.Text.Encoding.ASCII.GetBytes(hex)));
        var result = new char[hex.Length];
        for (var i = 0; i < hex.Length; i++)
        {
            result[i] = char.IsAsciiLetter(hex[i]) && Convert.ToInt32(hash[i].ToString(), 16) >= 8
                ? char.ToUpperInvariant(hex[i])
                : hex[i];
        }

        return "0x" + new string(result);
    }
}
