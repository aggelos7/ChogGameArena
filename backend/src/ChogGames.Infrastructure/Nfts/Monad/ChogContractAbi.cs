using System.Numerics;
using ChogGames.Core.Nfts;
using ChogGames.Core.Wallets;

namespace ChogGames.Infrastructure.Nfts.Monad;

/// <summary>Thrown when an RPC answer does not decode as the expected ABI value.</summary>
public sealed class AbiDecodingException(string message) : Exception(message);

/// <summary>
/// The few Solidity ABI encodings the provider needs: ERC-721 <c>ownerOf(uint256)</c> and Multicall3
/// <c>aggregate3((address,bool,bytes)[])</c>. Decoding treats the RPC answer as untrusted and checks every
/// offset and length.
/// </summary>
public static class ChogContractAbi
{
    private const int Word = 32;

    /// <summary><c>ownerOf(uint256)</c>.</summary>
    private static readonly byte[] OwnerOfSelector = [0x63, 0x52, 0x21, 0x1e];

    /// <summary><c>aggregate3((address,bool,bytes)[])</c>.</summary>
    private static readonly byte[] Aggregate3Selector = [0x82, 0xad, 0x56, 0xcb];

    public static byte[] EncodeOwnerOf(NftTokenId tokenId)
    {
        var data = new byte[4 + Word];
        OwnerOfSelector.CopyTo(data, 0);
        WriteUint(data.AsSpan(4, Word), tokenId.Value);
        return data;
    }

    /// <summary>The owner returned by <c>ownerOf</c>; null for the zero address.</summary>
    public static WalletAddress? DecodeOwner(ReadOnlySpan<byte> returnData)
    {
        if (returnData.Length != Word || returnData[..12].IndexOfAnyExcept((byte)0) >= 0)
        {
            throw new AbiDecodingException("ownerOf did not return an address.");
        }

        var address = returnData[12..];
        return address.IndexOfAnyExcept((byte)0) < 0 ? null : WalletAddress.FromBytes(address);
    }

    /// <summary>One <c>aggregate3</c> call that runs <c>ownerOf</c> for each token, allowing individual failures.</summary>
    public static byte[] EncodeAggregate3OwnerOf(WalletAddress contract, IReadOnlyList<NftTokenId> tokenIds)
    {
        // Each Call3 tuple: target, allowFailure, offset of callData (3 words), callData length, 36 bytes padded to 2 words.
        const int tupleSize = 6 * Word;
        var count = tokenIds.Count;
        var data = new byte[4 + (2 * Word) + (count * Word) + (count * tupleSize)];
        Aggregate3Selector.CopyTo(data, 0);
        var body = data.AsSpan(4);
        WriteUint(body[..Word], Word);
        WriteUint(body.Slice(Word, Word), count);

        var array = body[(2 * Word)..];
        var addressBytes = Convert.FromHexString(contract.Value.AsSpan(2));
        for (var i = 0; i < count; i++)
        {
            var tupleOffset = (count * Word) + (i * tupleSize);
            WriteUint(array.Slice(i * Word, Word), tupleOffset);

            var tuple = array.Slice(tupleOffset, tupleSize);
            addressBytes.CopyTo(tuple[12..Word]);
            WriteUint(tuple.Slice(Word, Word), 1);
            WriteUint(tuple.Slice(2 * Word, Word), 3 * Word);
            WriteUint(tuple.Slice(3 * Word, Word), 4 + Word);
            EncodeOwnerOf(tokenIds[i]).CopyTo(tuple[(4 * Word)..]);
        }

        return data;
    }

    /// <summary>
    /// Decodes <c>aggregate3</c>'s <c>(bool success, bytes returnData)[]</c>. A failed call (e.g. a burned or
    /// unminted token) yields null for that entry.
    /// </summary>
    public static IReadOnlyList<byte[]?> DecodeAggregate3(ReadOnlySpan<byte> data, int expectedCount)
    {
        var array = Slice(data, ReadLength(data, 0), data.Length);
        if (ReadLength(array, 0) != expectedCount)
        {
            throw new AbiDecodingException("Multicall3 returned a different number of results.");
        }

        var heads = array[Word..];
        var results = new byte[]?[expectedCount];
        for (var i = 0; i < expectedCount; i++)
        {
            var tuple = Slice(heads, ReadLength(heads, i * Word), heads.Length);
            var success = ReadLength(tuple, 0);
            if (success > 1)
            {
                throw new AbiDecodingException("Multicall3 returned an invalid success flag.");
            }

            var bytes = Slice(tuple, ReadLength(tuple, Word), tuple.Length);
            var length = ReadLength(bytes, 0);
            var returnData = Slice(bytes, Word, Word + length);
            results[i] = success == 1 ? returnData[..length].ToArray() : null;
        }

        return results;
    }

    private static void WriteUint(Span<byte> destination, BigInteger value)
    {
        destination.Clear();
        if (!value.TryWriteBytes(destination[(Word - value.GetByteCount(isUnsigned: true))..], out _, isUnsigned: true, isBigEndian: true))
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }
    }

    /// <summary>A word used as an offset, length or flag: must fit easily in an int.</summary>
    private static int ReadLength(ReadOnlySpan<byte> data, int position)
    {
        if (position < 0 || position > data.Length - Word)
        {
            throw new AbiDecodingException("ABI data is truncated.");
        }

        var word = data.Slice(position, Word);
        if (word[..28].IndexOfAnyExcept((byte)0) >= 0 || (word[28] & 0x80) != 0)
        {
            throw new AbiDecodingException("ABI offset or length is out of range.");
        }

        return (word[28] << 24) | (word[29] << 16) | (word[30] << 8) | word[31];
    }

    private static ReadOnlySpan<byte> Slice(ReadOnlySpan<byte> data, int start, int requiredEnd)
    {
        if (start > data.Length || requiredEnd > data.Length || requiredEnd < start)
        {
            throw new AbiDecodingException("ABI offset points outside the data.");
        }

        return data[start..];
    }
}
