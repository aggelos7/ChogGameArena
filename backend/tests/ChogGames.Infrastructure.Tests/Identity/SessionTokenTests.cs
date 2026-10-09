using System.Security.Cryptography;
using System.Text;
using ChogGames.Infrastructure.Identity;

namespace ChogGames.Infrastructure.Tests.Identity;

public class SessionTokenTests
{
    [Fact]
    public void Generated_tokens_are_unique_256_bit_base64url()
    {
        var tokens = Enumerable.Range(0, 1000).Select(_ => SessionToken.Generate()).ToArray();

        Assert.Equal(tokens.Length, tokens.Distinct().Count());
        Assert.All(tokens, token => Assert.Matches("^[A-Za-z0-9_-]{43}$", token));
    }

    [Fact]
    public void Hash_is_sha256_of_the_token()
    {
        var token = SessionToken.Generate();

        Assert.True(SessionToken.TryHash(token, out var hash));
        Assert.Equal(SHA256.HashData(Encoding.ASCII.GetBytes(token)), hash);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA+")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("3f8693d0-8f71-4dfe-b6fd-89276d8b4e21")]
    public void Malformed_tokens_are_rejected(string? token)
    {
        Assert.False(SessionToken.TryHash(token, out var hash));
        Assert.Empty(hash);
    }
}
