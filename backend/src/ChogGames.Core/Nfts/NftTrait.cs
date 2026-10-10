namespace ChogGames.Core.Nfts;

/// <summary>
/// One normalized metadata attribute (NFT-010). Any trait type or value is kept, including ones the app
/// does not know; traits never affect gameplay in the MVP (ADR-007).
/// </summary>
public sealed record NftTrait
{
    public const int MaxLength = 100;

    public NftTrait(string type, string value)
    {
        Type = Normalize(type, nameof(type));
        Value = Normalize(value, nameof(value));
    }

    public string Type { get; }

    public string Value { get; }

    private static string Normalize(string input, string name)
    {
        ArgumentNullException.ThrowIfNull(input, name);
        var value = input.Trim();
        if (value.Length is 0 or > MaxLength || value.Any(char.IsControl))
        {
            throw new ArgumentException($"A trait {name} must be 1–{MaxLength} printable characters.", name);
        }

        return value;
    }
}
