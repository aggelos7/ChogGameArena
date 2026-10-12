using System.Security.Cryptography;
using ChogGames.Core.Matches;

namespace ChogGames.Infrastructure.Matches;

/// <summary>Production randomness (ENG-008, STORY-029): the operating system's cryptographically secure generator.</summary>
public sealed class SecureRandomSource : IRandomSource
{
    public int Next(int minInclusive, int maxExclusive) => RandomNumberGenerator.GetInt32(minInclusive, maxExclusive);
}
