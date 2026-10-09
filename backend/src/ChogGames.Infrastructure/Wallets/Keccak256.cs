using Org.BouncyCastle.Crypto.Digests;

namespace ChogGames.Infrastructure.Wallets;

/// <summary>Ethereum's Keccak-256 (the original Keccak padding, not NIST SHA3-256).</summary>
public static class Keccak256
{
    public static byte[] Hash(ReadOnlySpan<byte> data)
    {
        var digest = new KeccakDigest(256);
        digest.BlockUpdate(data);
        var hash = new byte[32];
        digest.DoFinal(hash);
        return hash;
    }
}
