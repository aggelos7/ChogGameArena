using System.Globalization;
using ChogGames.Core.Wallets;

namespace ChogGames.Infrastructure.Wallets;

/// <summary>EIP-4361 (Sign-In with Ethereum) message text. The server builds and stores it; clients only sign it.</summary>
public static class SignInMessage
{
    public const string Statement =
        "Sign in to Chog Kingdom. This proves you own this wallet. It does not send a transaction or cost gas.";

    public static string Create(
        string domain,
        string uri,
        WalletAddress address,
        long chainId,
        string nonce,
        DateTimeOffset issuedAt,
        DateTimeOffset expiresAt) =>
        string.Join(
            '\n',
            $"{domain} wants you to sign in with your Ethereum account:",
            Eip55.ToChecksumAddress(address),
            string.Empty,
            Statement,
            string.Empty,
            $"URI: {uri}",
            "Version: 1",
            $"Chain ID: {chainId.ToString(CultureInfo.InvariantCulture)}",
            $"Nonce: {nonce}",
            $"Issued At: {Timestamp(issuedAt)}",
            $"Expiration Time: {Timestamp(expiresAt)}");

    private static string Timestamp(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
}
