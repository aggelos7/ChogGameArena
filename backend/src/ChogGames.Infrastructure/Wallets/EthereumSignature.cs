using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using ChogGames.Core.Wallets;
using Org.BouncyCastle.Asn1.X9;
using Org.BouncyCastle.Crypto.EC;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Math.EC;

namespace ChogGames.Infrastructure.Wallets;

/// <summary>
/// Recovers the signer of an EIP-191 <c>personal_sign</c> message (secp256k1 ECDSA with recovery id),
/// so the server can check that a signature was made by the claimed address. Malformed, non-canonical
/// (high-s) or unrecoverable signatures are rejected rather than guessed at.
/// </summary>
public static class EthereumSignature
{
    private static readonly X9ECParameters Curve = CustomNamedCurves.GetByName("secp256k1");
    private static readonly BigInteger HalfOrder = Curve.N.ShiftRight(1);

    /// <summary>The hash a wallet signs for <c>personal_sign</c>: keccak256("\x19Ethereum Signed Message:\n" + len + message).</summary>
    public static byte[] PersonalMessageHash(string message)
    {
        var body = Encoding.UTF8.GetBytes(message);
        var prefix = Encoding.UTF8.GetBytes("\u0019Ethereum Signed Message:\n" + body.Length.ToString(CultureInfo.InvariantCulture));
        return Keccak256.Hash([.. prefix, .. body]);
    }

    /// <summary>Parses a 65-byte <c>0x</c>-hex signature (r ‖ s ‖ v, v in 0/1/27/28) and recovers its signer.</summary>
    public static bool TryRecoverPersonalSigner(string message, string? signature, [NotNullWhen(true)] out WalletAddress? signer)
    {
        signer = null;
        if (!TryParse(signature, out var r, out var s, out var recoveryId))
        {
            return false;
        }

        return TryRecover(PersonalMessageHash(message), r, s, recoveryId, out signer);
    }

    private static bool TryParse(string? signature, out BigInteger r, out BigInteger s, out int recoveryId)
    {
        r = s = BigInteger.Zero;
        recoveryId = -1;
        if (signature is null || signature.Length != 132 || !signature.StartsWith("0x", StringComparison.Ordinal)
            || !signature.AsSpan(2).ToString().All(char.IsAsciiHexDigit))
        {
            return false;
        }

        var bytes = Convert.FromHexString(signature.AsSpan(2));
        r = new BigInteger(1, bytes, 0, 32);
        s = new BigInteger(1, bytes, 32, 32);
        recoveryId = bytes[64] switch
        {
            0 or 27 => 0,
            1 or 28 => 1,
            _ => -1,
        };

        // r, s in [1, n-1] and s in the lower half (EIP-2) so each signature has exactly one valid form.
        return recoveryId >= 0
            && r.SignValue > 0 && r.CompareTo(Curve.N) < 0
            && s.SignValue > 0 && s.CompareTo(HalfOrder) <= 0;
    }

    /// <summary>SEC 1 v2 §4.1.6 public key recovery for x = r (Ethereum only uses recovery ids 0 and 1).</summary>
    private static bool TryRecover(byte[] hash, BigInteger r, BigInteger s, int recoveryId, [NotNullWhen(true)] out WalletAddress? signer)
    {
        signer = null;
        ECPoint point;
        try
        {
            var compressed = new byte[33];
            compressed[0] = (byte)(recoveryId == 0 ? 0x02 : 0x03);
            var x = r.ToByteArrayUnsigned();
            x.CopyTo(compressed, 33 - x.Length);
            point = Curve.Curve.DecodePoint(compressed);
        }
        catch (ArgumentException)
        {
            return false; // r is not the x-coordinate of a curve point
        }

        var n = Curve.N;
        var e = new BigInteger(1, hash);
        var rInverse = r.ModInverse(n);
        var u1 = e.Negate().Mod(n).Multiply(rInverse).Mod(n);
        var u2 = s.Multiply(rInverse).Mod(n);
        var publicKey = ECAlgorithms.SumOfTwoMultiplies(Curve.G, u1, point, u2).Normalize();
        if (publicKey.IsInfinity)
        {
            return false;
        }

        var uncompressed = publicKey.GetEncoded(false);
        signer = WalletAddress.FromBytes(Keccak256.Hash(uncompressed.AsSpan(1)).AsSpan(12));
        return true;
    }
}
