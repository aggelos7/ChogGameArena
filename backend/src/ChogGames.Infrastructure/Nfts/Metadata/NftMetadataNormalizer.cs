using System.Globalization;
using System.Text.Json;
using ChogGames.Core.Nfts;

namespace ChogGames.Infrastructure.Nfts.Metadata;

/// <summary>
/// Turns an ERC-721 metadata JSON document into <see cref="NftMetadata"/> (NFT-010). Metadata is untrusted:
/// unknown trait types and values are kept as text, unusable entries are skipped, and a document that is not
/// a JSON object is <see cref="NftMetadataStatus.Invalid"/>. It never throws for bad input.
/// </summary>
public static class NftMetadataNormalizer
{
    public const int MaxDocumentBytes = 64 * 1024;
    public const int MaxTraits = 50;
    public const int MaxNameLength = 100;

    private static readonly JsonDocumentOptions Options = new() { MaxDepth = 16 };

    public static NftMetadata Normalize(IpfsUri metadataUri, ReadOnlySpan<byte> json)
    {
        if (json.Length > MaxDocumentBytes)
        {
            return NftMetadata.Invalid(metadataUri);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json.ToArray(), Options);
        }
        catch (JsonException)
        {
            return NftMetadata.Invalid(metadataUri);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return NftMetadata.Invalid(metadataUri);
            }

            var name = root.TryGetProperty("name", out var nameElement) ? Text(nameElement, MaxNameLength) : null;
            var image = root.TryGetProperty("image", out var imageElement)
                && imageElement.ValueKind == JsonValueKind.String
                && IpfsUri.TryParse(imageElement.GetString(), out var imageUri)
                    ? imageUri
                    : null;

            return new NftMetadata(NftMetadataStatus.Ok, metadataUri, name, image, Traits(root));
        }
    }

    private static List<NftTrait> Traits(JsonElement root)
    {
        var traits = new List<NftTrait>();
        if (!root.TryGetProperty("attributes", out var attributes) || attributes.ValueKind != JsonValueKind.Array)
        {
            return traits;
        }

        foreach (var attribute in attributes.EnumerateArray())
        {
            if (traits.Count == MaxTraits)
            {
                break;
            }

            if (attribute.ValueKind != JsonValueKind.Object
                || !attribute.TryGetProperty("trait_type", out var typeElement)
                || !attribute.TryGetProperty("value", out var valueElement))
            {
                continue;
            }

            var type = Text(typeElement, NftTrait.MaxLength);
            var value = Text(valueElement, NftTrait.MaxLength);
            if (type is not null && value is not null)
            {
                traits.Add(new NftTrait(type, value));
            }
        }

        return traits;
    }

    /// <summary>Strings, numbers and booleans as trimmed printable text, or null when unusable.</summary>
    private static string? Text(JsonElement element, int maxLength)
    {
        var text = element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.TryGetDecimal(out var number)
                ? number.ToString(CultureInfo.InvariantCulture)
                : element.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        };

        text = text?.Trim();
        return string.IsNullOrEmpty(text) || text.Length > maxLength || text.Any(char.IsControl) ? null : text;
    }
}
