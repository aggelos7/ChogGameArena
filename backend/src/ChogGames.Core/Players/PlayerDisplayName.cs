using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace ChogGames.Core.Players;

/// <summary>Validated public display name: 3–20 letters, digits, spaces, '_' or '-'; trimmed, no repeated spaces.</summary>
public sealed partial record PlayerDisplayName
{
    public const int MinLength = 3;
    public const int MaxLength = 20;

    public const string Rule =
        "Display name must be 3–20 characters using letters, digits, spaces, '_' or '-', and must not start or end with a space.";

    private PlayerDisplayName(string value) => Value = value;

    public string Value { get; }

    public static bool TryCreate(string? input, [NotNullWhen(true)] out PlayerDisplayName? displayName)
    {
        displayName = null;
        if (input is null)
        {
            return false;
        }

        var value = input.Trim();
        if (value.Length is < MinLength or > MaxLength || !AllowedPattern().IsMatch(value))
        {
            return false;
        }

        displayName = new PlayerDisplayName(value);
        return true;
    }

    /// <summary>Default guest name such as "Chog 4821"; names are not unique.</summary>
    public static PlayerDisplayName GenerateGuestName() =>
        new($"Chog {RandomNumberGenerator.GetInt32(1000, 10000)}");

    public override string ToString() => Value;

    [GeneratedRegex(@"^[\p{L}\p{Nd}_-]+( [\p{L}\p{Nd}_-]+)*$")]
    private static partial Regex AllowedPattern();
}
