using System.Security.Cryptography;
using ChogGames.Core.Wallets;
using ChogGames.Infrastructure.Wallets;
using Org.BouncyCastle.Asn1.X9;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.EC;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Math;

namespace ChogGames.Tests.Wallets;

/// <summary>Test-only EOA: signs EIP-191 personal messages exactly like a browser wallet.</summary>
public sealed class TestWallet
{
    private static readonly X9ECParameters Curve = CustomNamedCurves.GetByName("secp256k1");
    private static readonly ECDomainParameters Domain = new(Curve.Curve, Curve.G, Curve.N, Curve.H);
    private readonly BigInteger privateKey;

    public TestWallet(string privateKeyHex)
    {
        privateKey = new BigInteger(1, Convert.FromHexString(privateKeyHex.Replace("0x", string.Empty)));
        var publicKey = Curve.G.Multiply(privateKey).Normalize().GetEncoded(false);
        Address = WalletAddress.FromBytes(Keccak256.Hash(publicKey.AsSpan(1)).AsSpan(12));
    }

    public WalletAddress Address { get; }

    public string ChecksumAddress => Eip55.ToChecksumAddress(Address);

    public static TestWallet Random()
    {
        BigInteger key;
        do
        {
            key = new BigInteger(1, RandomNumberGenerator.GetBytes(32));
        }
        while (key.SignValue == 0 || key.CompareTo(Curve.N) >= 0);

        return new TestWallet(Convert.ToHexString(key.ToByteArrayUnsigned()));
    }

    /// <summary>A 0x-hex r ‖ s ‖ v signature (v = 27/28, low s) as returned by <c>personal_sign</c>.</summary>
    public string SignPersonal(string message)
    {
        var signer = new ECDsaSigner(new HMacDsaKCalculator(new Sha256Digest()));
        signer.Init(true, new ECPrivateKeyParameters(privateKey, Domain));
        var rs = signer.GenerateSignature(EthereumSignature.PersonalMessageHash(message));
        var r = rs[0];
        var s = rs[1].CompareTo(Curve.N.ShiftRight(1)) > 0 ? Curve.N.Subtract(rs[1]) : rs[1];

        foreach (var v in new byte[] { 27, 28 })
        {
            var signature = Format(r, s, v);
            if (EthereumSignature.TryRecoverPersonalSigner(message, signature, out var recovered) && recovered == Address)
            {
                return signature;
            }
        }

        throw new InvalidOperationException("No recovery id reproduces the signer.");
    }

    /// <summary>The same signature with s replaced by n - s (the malleable high-s twin).</summary>
    public static string ToHighS(string signature)
    {
        var bytes = Convert.FromHexString(signature[2..]);
        var s = Curve.N.Subtract(new BigInteger(1, bytes, 32, 32));
        var v = (byte)(bytes[64] == 27 ? 28 : 27);
        return Format(new BigInteger(1, bytes, 0, 32), s, v);
    }

    private static string Format(BigInteger r, BigInteger s, byte v)
    {
        var bytes = new byte[65];
        var rBytes = r.ToByteArrayUnsigned();
        var sBytes = s.ToByteArrayUnsigned();
        rBytes.CopyTo(bytes, 32 - rBytes.Length);
        sBytes.CopyTo(bytes, 64 - sBytes.Length);
        bytes[64] = v;
        return "0x" + Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
