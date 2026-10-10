using System.Numerics;
using ChogGames.Core.Nfts;
using ChogGames.Core.Wallets;
using ChogGames.Infrastructure.Nfts.Monad;

namespace ChogGames.Infrastructure.Tests.Nfts.Monad;

public class ChogContractAbiTests
{
    private static readonly WalletAddress Contract = Address("0xc96d31f8626c6d03fae5dcd3d61e3fb9f4a73763");

    // aggregate3 for ownerOf(1) and ownerOf(1969), encoded independently in JavaScript and accepted by the
    // Multicall3 deployment on Monad Mainnet.
    private const string Aggregate3Call = "0x82ad56cb0000000000000000000000000000000000000000000000000000000000000020000000000000000000000000000000000000000000000000000000000000000200000000000000000000000000000000000000000000000000000000000000400000000000000000000000000000000000000000000000000000000000000100000000000000000000000000c96d31f8626c6d03fae5dcd3d61e3fb9f4a737630000000000000000000000000000000000000000000000000000000000000001000000000000000000000000000000000000000000000000000000000000006000000000000000000000000000000000000000000000000000000000000000246352211e000000000000000000000000000000000000000000000000000000000000000100000000000000000000000000000000000000000000000000000000000000000000000000000000c96d31f8626c6d03fae5dcd3d61e3fb9f4a737630000000000000000000000000000000000000000000000000000000000000001000000000000000000000000000000000000000000000000000000000000006000000000000000000000000000000000000000000000000000000000000000246352211e00000000000000000000000000000000000000000000000000000000000007b100000000000000000000000000000000000000000000000000000000";

    // Real Monad Mainnet answer to aggregate3 for ownerOf(1) and ownerOf(1970) on 2026-10-10: token 1 is owned,
    // token 1970 does not exist and reverts with ERC721NonexistentToken(1970).
    private const string Aggregate3Answer = "0x00000000000000000000000000000000000000000000000000000000000000200000000000000000000000000000000000000000000000000000000000000002000000000000000000000000000000000000000000000000000000000000004000000000000000000000000000000000000000000000000000000000000000c00000000000000000000000000000000000000000000000000000000000000001000000000000000000000000000000000000000000000000000000000000004000000000000000000000000000000000000000000000000000000000000000200000000000000000000000003d38fee3e1a4c43d8fcf715aa6c9ff025074e7e00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000004000000000000000000000000000000000000000000000000000000000000000247e27328900000000000000000000000000000000000000000000000000000000000007b200000000000000000000000000000000000000000000000000000000";

    private static WalletAddress Address(string value)
    {
        Assert.True(WalletAddress.TryParse(value, out var address));
        return address;
    }

    private static byte[] Hex(string value) => Convert.FromHexString(value.AsSpan(2));

    private static string Word(BigInteger value) => value.ToString("x64")[^64..];

    [Fact]
    public void Encodes_owner_of()
    {
        Assert.Equal(
            "6352211e" + new string('0', 61) + "7b1",
            Convert.ToHexString(ChogContractAbi.EncodeOwnerOf(NftTokenId.From(1969))).ToLowerInvariant());
    }

    [Fact]
    public void Encodes_aggregate3_like_the_reference_vector()
    {
        var data = ChogContractAbi.EncodeAggregate3OwnerOf(Contract, [NftTokenId.From(1), NftTokenId.From(1969)]);

        Assert.Equal(Aggregate3Call, "0x" + Convert.ToHexString(data).ToLowerInvariant());
    }

    [Fact]
    public void Decodes_a_real_aggregate3_answer_with_a_failed_entry()
    {
        var results = ChogContractAbi.DecodeAggregate3(Hex(Aggregate3Answer), expectedCount: 2);

        Assert.Equal(Address("0x3d38fee3e1a4c43d8fcf715aa6c9ff025074e7e0"), ChogContractAbi.DecodeOwner(results[0]!));
        Assert.Null(results[1]);
    }

    [Fact]
    public void Zero_address_owner_means_no_owner()
    {
        Assert.Null(ChogContractAbi.DecodeOwner(new byte[32]));
    }

    [Theory]
    [InlineData(31)]
    [InlineData(33)]
    [InlineData(0)]
    public void Owner_must_be_exactly_one_word(int length)
    {
        Assert.Throws<AbiDecodingException>(() => ChogContractAbi.DecodeOwner(new byte[length]));
    }

    [Fact]
    public void Owner_with_dirty_high_bytes_is_rejected()
    {
        var word = new byte[32];
        word[0] = 1;
        word[31] = 1;

        Assert.Throws<AbiDecodingException>(() => ChogContractAbi.DecodeOwner(word));
    }

    [Fact]
    public void Wrong_result_count_is_rejected()
    {
        Assert.Throws<AbiDecodingException>(() => ChogContractAbi.DecodeAggregate3(Hex(Aggregate3Answer), expectedCount: 3));
    }

    [Fact]
    public void Truncated_answers_are_rejected_at_every_length()
    {
        var answer = Hex(Aggregate3Answer);
        // The last 28 bytes only pad the final returnData, so every shorter answer is missing real data.
        for (var length = 0; length < answer.Length - 28; length++)
        {
            var truncated = answer[..length];
            Assert.Throws<AbiDecodingException>(() => ChogContractAbi.DecodeAggregate3(truncated, expectedCount: 2));
        }
    }

    [Theory]
    [InlineData(0, "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff")]
    [InlineData(0, "00000000000000000000000000000000000000000000000000000000ffffffff")]
    [InlineData(32, "0000000000000000000000000000000000000000000000000000000000100000")]
    [InlineData(128, "0000000000000000000000000000000000000000000000000000000000000002")]
    public void Out_of_range_offsets_lengths_and_flags_are_rejected(int position, string word)
    {
        var answer = Hex(Aggregate3Answer);
        // Positions: 0 = array offset, 32 = array length (count), 128 = success flag of the first result.
        Convert.FromHexString(word).CopyTo(answer, position);

        Assert.Throws<AbiDecodingException>(() => ChogContractAbi.DecodeAggregate3(answer, expectedCount: 2));
    }

    [Fact]
    public void Round_trips_through_an_independent_decoder()
    {
        var data = ChogContractAbi.EncodeAggregate3OwnerOf(Contract, [NftTokenId.From(0), NftTokenId.From(42), NftTokenId.From(ulong.MaxValue)]);

        Assert.Equal(["0", "42", ulong.MaxValue.ToString()], FakeMonadRpc.DecodeOwnerOfCalls(data, Contract).Select(id => id.ToString()));
        Assert.Equal(Word(42), Convert.ToHexString(ChogContractAbi.EncodeOwnerOf(NftTokenId.From(42))[4..]).ToLowerInvariant());
    }

    // Real Monad Mainnet answer to tokenURI(1) on 2026-10-10.
    private const string TokenUriAnswer = "0x0000000000000000000000000000000000000000000000000000000000000020000000000000000000000000000000000000000000000000000000000000004c697066733a2f2f6261667962656964347962756a6e73636469706b6e6f37707335757471656c70357279357279726571686179337a68676b33613678346a727571752f313436322e6a736f6e0000000000000000000000000000000000000000";

    [Fact]
    public void Encodes_token_uri()
    {
        Assert.Equal(
            "c87b56dd" + new string('0', 63) + "1",
            Convert.ToHexString(ChogContractAbi.EncodeTokenUri(NftTokenId.From(1))).ToLowerInvariant());
    }

    [Fact]
    public void Decodes_a_real_token_uri()
    {
        Assert.Equal(
            "ipfs://bafybeid4ybujnscdipkno7ps5utqelp5ry5ryreqhay3zhgk3a6x4jruqu/1462.json",
            ChogContractAbi.DecodeString(Hex(TokenUriAnswer)));
    }

    [Fact]
    public void Truncated_or_oversized_strings_are_rejected()
    {
        var answer = Hex(TokenUriAnswer);
        Assert.Throws<AbiDecodingException>(() => ChogContractAbi.DecodeString(answer[..100]));

        var tooLong = new byte[64 + ChogContractAbi.MaxStringLength + 32];
        tooLong[31] = 0x20;
        var length = ChogContractAbi.MaxStringLength + 1;
        tooLong[62] = (byte)(length >> 8);
        tooLong[63] = (byte)length;
        Assert.Throws<AbiDecodingException>(() => ChogContractAbi.DecodeString(tooLong));
    }

    [Fact]
    public void Invalid_utf8_is_rejected()
    {
        var data = new byte[96];
        data[31] = 0x20;
        data[63] = 2;
        data[64] = 0xc3;
        data[65] = 0x28;

        Assert.Throws<AbiDecodingException>(() => ChogContractAbi.DecodeString(data));
    }
}
