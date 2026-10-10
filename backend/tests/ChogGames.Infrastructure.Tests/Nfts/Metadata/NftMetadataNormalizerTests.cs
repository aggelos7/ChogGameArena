using System.Text;
using ChogGames.Infrastructure.Nfts.Metadata;

namespace ChogGames.Infrastructure.Tests.Nfts.Metadata;

public class NftMetadataNormalizerTests
{
    private static readonly IpfsUri Source = Uri("ipfs://bafybeid4ybujnscdipkno7ps5utqelp5ry5ryreqhay3zhgk3a6x4jruqu/1462.json");

    private static IpfsUri Uri(string value)
    {
        Assert.True(IpfsUri.TryParse(value, out var uri));
        return uri;
    }

    private static NftMetadata Normalize(string json) => NftMetadataNormalizer.Normalize(Source, Encoding.UTF8.GetBytes(json));

    [Fact]
    public void Real_chog_metadata_is_normalized()
    {
        // Abbreviated copy of the real metadata of token 1 (CHOG #1462), read 2026-10-10.
        var metadata = Normalize("""
            {
              "name": "CHOG #1462",
              "description": "CHOG — a 1969-piece collection on Monad.",
              "image": "ipfs://bafybeihau2egcccbdxymeckmmbrqytbfor5gkt3en6d5fsbueymdbq7g6e/1462.webp",
              "attributes": [
                { "trait_type": "Background", "value": "Deep Aqua" },
                { "trait_type": "Body", "value": "Yellow 143 Jersey" },
                { "trait_type": "Tier", "value": "Uncommon" },
                { "trait_type": "Naked", "value": "No" }
              ]
            }
            """);

        Assert.Equal(NftMetadataStatus.Ok, metadata.Status);
        Assert.Equal(Source, metadata.MetadataUri);
        Assert.Equal("CHOG #1462", metadata.Name);
        Assert.Equal("ipfs://bafybeihau2egcccbdxymeckmmbrqytbfor5gkt3en6d5fsbueymdbq7g6e/1462.webp", metadata.Image?.ToString());
        Assert.Equal(
            ["Background=Deep Aqua", "Body=Yellow 143 Jersey", "Tier=Uncommon", "Naked=No"],
            metadata.Traits.Select(t => $"{t.Type}={t.Value}"));
    }

    [Fact]
    public void Unknown_traits_and_non_string_values_survive()
    {
        var metadata = Normalize("""
            {"attributes": [
              {"trait_type": "Brand New Trait", "value": "Never Seen ✨"},
              {"trait_type": "Level", "value": 7, "display_type": "number"},
              {"trait_type": "Power", "value": 1.50},
              {"trait_type": "Huge", "value": 1e400},
              {"trait_type": "Shiny", "value": true},
              {"trait_type": "  Padded  ", "value": "  x  "}
            ]}
            """);

        Assert.Equal(
            ["Brand New Trait=Never Seen ✨", "Level=7", "Power=1.50", "Huge=1e400", "Shiny=true", "Padded=x"],
            metadata.Traits.Select(t => $"{t.Type}={t.Value}"));
    }

    [Fact]
    public void Unusable_trait_entries_are_skipped()
    {
        var metadata = Normalize($$$"""
            {"attributes": [
              "Background",
              {"value": "no type"},
              {"trait_type": "No value"},
              {"trait_type": "", "value": "empty type"},
              {"trait_type": "Null", "value": null},
              {"trait_type": "Object", "value": {"a": 1}},
              {"trait_type": "Array", "value": [1]},
              {"trait_type": "Control", "value": "a\u0007b"},
              {"trait_type": "Long", "value": "{{{new string('x', 101)}}}"},
              {"trait_type": "Kept", "value": "yes"}
            ]}
            """);

        Assert.Equal(["Kept=yes"], metadata.Traits.Select(t => $"{t.Type}={t.Value}"));
    }

    [Fact]
    public void Trait_count_is_capped()
    {
        var attributes = string.Join(",", Enumerable.Range(0, 80).Select(i => $$"""{"trait_type":"T{{i}}","value":"{{i}}"}"""));

        Assert.Equal(NftMetadataNormalizer.MaxTraits, Normalize($$"""{"attributes":[{{attributes}}]}""").Traits.Count);
    }

    [Theory]
    [InlineData("""{"name": "Chog", "image": "https://evil.example/x.png"}""")]
    [InlineData("""{"name": "Chog", "image": "data:image/svg+xml,<svg/>"}""")]
    [InlineData("""{"name": "Chog", "image": 5}""")]
    [InlineData("""{"name": "Chog", "image": "ipfs://bad cid/x.png"}""")]
    [InlineData("""{"name": "Chog"}""")]
    public void Missing_or_unsafe_images_become_no_image(string json)
    {
        var metadata = Normalize(json);

        Assert.Equal(NftMetadataStatus.Ok, metadata.Status);
        Assert.Equal("Chog", metadata.Name);
        Assert.Null(metadata.Image);
    }

    [Theory]
    [InlineData("""{"name": 12}""", "12")]
    [InlineData("""{"name": ""}""", null)]
    [InlineData("""{"name": {"en": "Chog"}}""", null)]
    [InlineData("""{"name": "Line\nBreak"}""", null)]
    public void Name_is_normalized_or_dropped(string json, string? expected)
    {
        Assert.Equal(expected, Normalize(json).Name);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("\"text\"")]
    [InlineData("null")]
    [InlineData("{\"name\": \"Chog\"")]
    [InlineData("{\"a\":[[[[[[[[[[[[[[[[[[[[1]]]]]]]]]]]]]]]]]]]}")]
    public void Documents_that_are_not_a_json_object_are_invalid(string json)
    {
        var metadata = Normalize(json);

        Assert.Equal(NftMetadataStatus.Invalid, metadata.Status);
        Assert.Equal(Source, metadata.MetadataUri);
        Assert.Empty(metadata.Traits);
    }

    [Fact]
    public void Oversized_documents_are_invalid()
    {
        var json = $$"""{"name": "Chog", "description": "{{new string('x', NftMetadataNormalizer.MaxDocumentBytes)}}"}""";

        Assert.Equal(NftMetadataStatus.Invalid, Normalize(json).Status);
    }
}
