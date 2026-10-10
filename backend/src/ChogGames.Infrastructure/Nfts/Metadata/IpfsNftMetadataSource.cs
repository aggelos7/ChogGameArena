using System.Net;
using ChogGames.Core.Nfts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ChogGames.Infrastructure.Nfts.Metadata;

/// <summary>
/// Reads metadata as <c>tokenURI</c> on chain, then the JSON document through the configured IPFS gateways in
/// order (STORY-016). Only <c>ipfs://</c> URIs are followed and gateway redirects are not, so requests only ever
/// go to configured hosts. Documents are size-capped and normalized by <see cref="NftMetadataNormalizer"/>.
/// </summary>
public sealed class IpfsNftMetadataSource(
    ITokenUriReader tokenUris,
    IHttpClientFactory httpClients,
    IOptions<NftOptions> options,
    ILogger<IpfsNftMetadataSource> logger) : INftMetadataSource
{
    public const string HttpClientName = "ipfs-gateway";

    public async Task<NftMetadata> FetchAsync(NftTokenId tokenId, CancellationToken cancellationToken)
    {
        string? tokenUri;
        try
        {
            tokenUri = await tokenUris.GetTokenUriAsync(tokenId, cancellationToken);
        }
        catch (NftProviderUnavailableException)
        {
            return NftMetadata.Unavailable();
        }

        if (!IpfsUri.TryParse(tokenUri, out var metadataUri))
        {
            logger.LogWarning("Token {TokenId} has no usable ipfs:// token URI.", tokenId);
            return NftMetadata.Invalid();
        }

        var client = httpClients.CreateClient(HttpClientName);
        var gateways = options.Value.Ipfs.Gateways;
        for (var i = 0; i < gateways.Count; i++)
        {
            var document = await TryDownloadAsync(client, metadataUri.ToGatewayUrl(gateways[i]), i, cancellationToken);
            if (document is not null)
            {
                var metadata = NftMetadataNormalizer.Normalize(metadataUri, document);
                if (metadata.Status == NftMetadataStatus.Invalid)
                {
                    logger.LogWarning("Token {TokenId} metadata {MetadataUri} is not a usable JSON object.", tokenId, metadataUri);
                }

                return metadata;
            }
        }

        return NftMetadata.Unavailable(metadataUri);
    }

    /// <returns>The document, or null when this gateway failed.</returns>
    private async Task<byte[]?> TryDownloadAsync(HttpClient client, Uri url, int gateway, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                // Gateway URLs are not logged: a dedicated gateway may carry a key.
                logger.LogWarning("IPFS gateway {Gateway} answered HTTP {Status}.", gateway, (int)response.StatusCode);
                return null;
            }

            if (response.Content.Headers.ContentLength > NftMetadataNormalizer.MaxDocumentBytes)
            {
                // Too large to be metadata: report as a document the normalizer rejects.
                return new byte[NftMetadataNormalizer.MaxDocumentBytes + 1];
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var buffer = new byte[NftMetadataNormalizer.MaxDocumentBytes + 1];
            var total = 0;
            int read;
            while (total < buffer.Length && (read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken)) > 0)
            {
                total += read;
            }

            return buffer[..total];
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("IPFS gateway {Gateway} timed out.", gateway);
            return null;
        }
        catch (HttpRequestException error)
        {
            logger.LogWarning("IPFS gateway {Gateway} could not be reached ({Error}).", gateway, error.HttpRequestError);
            return null;
        }
    }
}
