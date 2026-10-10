using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using ChogGames.Core.Wallets;

namespace ChogGames.Infrastructure.Nfts.Monad;

/// <summary>The RPC answered, but the call reverted (e.g. <c>ownerOf</c> for a token that does not exist).</summary>
public sealed class RpcRevertedException() : Exception("The call reverted.");

/// <summary>
/// The RPC could not be used: transport failure, timeout, HTTP or JSON-RPC error, or a malformed answer.
/// Messages never include the endpoint URL, which may carry a provider key.
/// </summary>
public sealed class RpcUnavailableException(string message) : Exception(message);

/// <summary>Minimal read-only Ethereum JSON-RPC client: <c>eth_chainId</c> and <c>eth_call</c> at the latest block.</summary>
public sealed class MonadRpcClient(HttpClient http)
{
    public const string HttpClientName = "monad-rpc";

    /// <summary>Largest response accepted; a full collection scan is about 300 KiB.</summary>
    private const int MaxResponseBytes = 4 * 1024 * 1024;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<long> GetChainIdAsync(CancellationToken cancellationToken)
    {
        var result = await SendAsync("eth_chainId", [], cancellationToken);
        return result is { Length: > 2 and <= 18 } && result.StartsWith("0x", StringComparison.Ordinal)
            && long.TryParse(result.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var chainId)
            && chainId > 0
                ? chainId
                : throw new RpcUnavailableException("eth_chainId returned a malformed value.");
    }

    /// <exception cref="RpcRevertedException">The call reverted.</exception>
    public async Task<byte[]> CallAsync(WalletAddress to, byte[] data, CancellationToken cancellationToken)
    {
        var call = new Dictionary<string, string> { ["to"] = to.Value, ["data"] = "0x" + Convert.ToHexString(data).ToLowerInvariant() };
        var result = await SendAsync("eth_call", [call, "latest"], cancellationToken);
        if (result is null || result.Length % 2 != 0 || !result.StartsWith("0x", StringComparison.Ordinal))
        {
            throw new RpcUnavailableException("eth_call returned a malformed value.");
        }

        try
        {
            return Convert.FromHexString(result.AsSpan(2));
        }
        catch (FormatException)
        {
            throw new RpcUnavailableException("eth_call returned a malformed value.");
        }
    }

    private async Task<string?> SendAsync(string method, object[] parameters, CancellationToken cancellationToken)
    {
        var request = new RpcRequest("2.0", 1, method, parameters);
        RpcResponse? response;
        try
        {
            using var message = await http.PostAsJsonAsync(string.Empty, request, Json, cancellationToken);
            if (!message.IsSuccessStatusCode)
            {
                throw new RpcUnavailableException($"{method} failed with HTTP {(int)message.StatusCode}.");
            }

            if (message.Content.Headers.ContentLength > MaxResponseBytes)
            {
                throw new RpcUnavailableException($"{method} response is too large.");
            }

            await using var stream = await message.Content.ReadAsStreamAsync(cancellationToken);
            using var limited = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                if (limited.Length + read > MaxResponseBytes)
                {
                    throw new RpcUnavailableException($"{method} response is too large.");
                }

                limited.Write(buffer, 0, read);
            }

            response = JsonSerializer.Deserialize<RpcResponse>(limited.GetBuffer().AsSpan(0, (int)limited.Length), Json);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new RpcUnavailableException($"{method} timed out.");
        }
        catch (HttpRequestException error)
        {
            throw new RpcUnavailableException($"{method} could not reach the RPC ({error.HttpRequestError}).");
        }
        catch (JsonException)
        {
            throw new RpcUnavailableException($"{method} returned invalid JSON.");
        }

        if (response?.Error is { } rpcError)
        {
            if (rpcError.Code == 3 || rpcError.Message?.Contains("revert", StringComparison.OrdinalIgnoreCase) == true)
            {
                throw new RpcRevertedException();
            }

            throw new RpcUnavailableException($"{method} returned JSON-RPC error {rpcError.Code}.");
        }

        if (response is null || response.Id != 1 || response.Result.ValueKind != JsonValueKind.String)
        {
            throw new RpcUnavailableException($"{method} returned no result.");
        }

        return response.Result.GetString();
    }

    private sealed record RpcRequest(
        [property: JsonPropertyName("jsonrpc")] string JsonRpc,
        int Id,
        string Method,
        [property: JsonPropertyName("params")] object[] Parameters);

    private sealed record RpcResponse(int? Id, JsonElement Result, RpcError? Error);

    private sealed record RpcError(long Code, string? Message);
}
