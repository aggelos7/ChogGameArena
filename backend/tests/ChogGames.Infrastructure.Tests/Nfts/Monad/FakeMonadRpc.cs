using System.Net;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ChogGames.Core.Wallets;

namespace ChogGames.Infrastructure.Tests.Nfts.Monad;

/// <summary>
/// In-memory Monad JSON-RPC endpoint for an ERC-721 contract and Multicall3. Its ABI handling is written
/// independently of the provider's so the two check each other.
/// </summary>
public sealed class FakeMonadRpc(WalletAddress contract, WalletAddress multicall) : HttpMessageHandler
{
    public long ChainId { get; set; } = 143;

    /// <summary>Current owner of each existing token; tokens not listed do not exist.</summary>
    public Dictionary<BigInteger, WalletAddress> Owners { get; } = [];

    /// <summary>Replaces the next answers: return a response (or throw) instead of answering normally.</summary>
    public Func<string, HttpResponseMessage>? Override { get; set; }

    public Task? Delay { get; set; }

    public List<string> Calls { get; } = [];

    /// <summary>The token URI returned for an existing token.</summary>
    public Func<BigInteger, string> TokenUri { get; set; } = id => $"ipfs://bafybeid4ybujnscdipkno7ps5utqelp5ry5ryreqhay3zhgk3a6x4jruqu/{id}.json";

    public int Count(string kind) => Calls.Count(call => call == kind);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!;
        var method = body["method"]!.GetValue<string>();
        string kind;
        JsonNode? result = null;
        JsonObject? error = null;

        if (method == "eth_chainId")
        {
            kind = "chainId";
            result = $"0x{ChainId:x}";
        }
        else if (method == "eth_call")
        {
            var call = body["params"]![0]!;
            Assert.Equal("latest", body["params"]![1]!.GetValue<string>());
            var to = call["to"]!.GetValue<string>();
            var data = Convert.FromHexString(call["data"]!.GetValue<string>().AsSpan(2));
            if (to == multicall.Value)
            {
                kind = "multicall";
                result = "0x" + Convert.ToHexString(EncodeAggregate3Results(DecodeOwnerOfCalls(data, contract))).ToLowerInvariant();
            }
            else if (Convert.ToHexString(data[..4]).ToLowerInvariant() == "c87b56dd")
            {
                Assert.Equal(contract.Value, to);
                kind = "tokenURI";
                var tokenId = Uint(data[4..], 0);
                if (Owners.ContainsKey(tokenId))
                {
                    result = "0x" + EncodeString(TokenUri(tokenId));
                }
                else
                {
                    error = new JsonObject { ["code"] = 3, ["message"] = "execution reverted" };
                }
            }
            else
            {
                Assert.Equal(contract.Value, to);
                kind = "ownerOf";
                var tokenId = DecodeOwnerOf(data);
                if (Owners.TryGetValue(tokenId, out var owner))
                {
                    result = "0x" + Convert.ToHexString(OwnerWord(owner)).ToLowerInvariant();
                }
                else
                {
                    error = new JsonObject { ["code"] = 3, ["message"] = "execution reverted", ["data"] = "0x7e273289" };
                }
            }
        }
        else
        {
            throw new InvalidOperationException($"Unexpected method {method}.");
        }

        Calls.Add(kind);
        if (Delay is not null)
        {
            await Delay.WaitAsync(cancellationToken);
        }

        if (Override is not null)
        {
            return Override(kind);
        }

        var response = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = body["id"]!.GetValue<int>() };
        if (error is null)
        {
            response["result"] = result;
        }
        else
        {
            response["error"] = error;
        }

        return Json(response.ToJsonString());
    }

    public static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    /// <summary>Token ids of an <c>aggregate3</c> call whose every entry is <c>contract.ownerOf(id)</c> with allowFailure.</summary>
    public static IReadOnlyList<BigInteger> DecodeOwnerOfCalls(ReadOnlySpan<byte> data, WalletAddress contract)
    {
        Assert.Equal("82ad56cb", Convert.ToHexString(data[..4]).ToLowerInvariant());
        var args = data[4..];
        var array = args[(int)Uint(args, 0)..];
        var count = (int)Uint(array, 0);
        var heads = array[32..];
        var ids = new List<BigInteger>();
        for (var i = 0; i < count; i++)
        {
            var tuple = heads[(int)Uint(heads, i * 32)..];
            Assert.Equal(contract, WalletAddress.FromBytes(tuple[12..32]));
            Assert.Equal(BigInteger.One, Uint(tuple, 32));
            var bytes = tuple[(int)Uint(tuple, 64)..];
            var length = (int)Uint(bytes, 0);
            ids.Add(DecodeOwnerOf(bytes.Slice(32, length)));
        }

        return ids;
    }

    private static BigInteger DecodeOwnerOf(ReadOnlySpan<byte> data)
    {
        Assert.Equal(36, data.Length);
        Assert.Equal("6352211e", Convert.ToHexString(data[..4]).ToLowerInvariant());
        return Uint(data[4..], 0);
    }

    private byte[] EncodeAggregate3Results(IReadOnlyList<BigInteger> tokenIds)
    {
        // (bool success, bytes returnData)[]: offset, length, element offsets, then each element.
        var elements = tokenIds.Select(id => Owners.TryGetValue(id, out var owner)
            ? Word(1) + Word(64) + Word(32) + Hex(OwnerWord(owner))
            : Word(0) + Word(64) + Word(36) + "7e273289" + Word(id) + new string('0', 56)).ToList();
        var head = new StringBuilder(Word(32) + Word(elements.Count));
        var offset = elements.Count * 32;
        foreach (var element in elements)
        {
            head.Append(Word(offset));
            offset += element.Length / 2;
        }

        return Convert.FromHexString(head + string.Concat(elements));
    }

    private static string EncodeString(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        var padded = (bytes.Length + 31) / 32 * 32;
        return Word(32) + Word(bytes.Length) + Hex(bytes) + new string('0', (padded - bytes.Length) * 2);
    }

    private static byte[] OwnerWord(WalletAddress owner) => Convert.FromHexString(new string('0', 24) + owner.Value[2..]);

    private static BigInteger Uint(ReadOnlySpan<byte> data, int position) =>
        new(data.Slice(position, 32), isUnsigned: true, isBigEndian: true);

    private static string Word(BigInteger value) => value.ToString("x64")[^64..];

    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();
}
