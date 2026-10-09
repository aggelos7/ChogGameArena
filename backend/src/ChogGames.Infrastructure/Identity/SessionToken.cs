using System.Security.Cryptography;

namespace ChogGames.Infrastructure.Identity;

/// <summary>Opaque 256-bit bearer token (base64url) and its SHA-256 hash for storage and lookup.</summary>
public static class SessionToken
{
    private const int TokenBytes = 32;

    public static string Generate() => Base64UrlEncode(RandomNumberGenerator.GetBytes(TokenBytes));

    /// <summary>Returns false for anything that is not a well-formed token, without hashing it.</summary>
    public static bool TryHash(string? token, out byte[] hash)
    {
        hash = [];
        if (string.IsNullOrEmpty(token) || token.Length != 43 || !IsBase64Url(token))
        {
            return false;
        }

        hash = SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(token));
        return true;
    }

    private static bool IsBase64Url(string value) =>
        value.All(c => c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_');

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
