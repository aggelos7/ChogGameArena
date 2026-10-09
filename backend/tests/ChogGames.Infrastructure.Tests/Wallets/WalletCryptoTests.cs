using System.Text;
using ChogGames.Core.Wallets;
using ChogGames.Infrastructure.Wallets;
using ChogGames.Tests.Wallets;

namespace ChogGames.Infrastructure.Tests.Wallets;

public class WalletCryptoTests
{
    private static WalletAddress Address(string value)
    {
        Assert.True(WalletAddress.TryParse(value, out var address));
        return address;
    }

    [Theory]
    [InlineData("", "c5d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470")]
    [InlineData("abc", "4e03657aea45a94fc7d47ba826c8d667c0d1e6e33a64a036ec44f58fa12d6c45")]
    public void Keccak256_matches_published_vectors(string input, string expected)
    {
        Assert.Equal(expected, Convert.ToHexString(Keccak256.Hash(Encoding.ASCII.GetBytes(input))).ToLowerInvariant());
    }

    [Theory]
    [InlineData("0x5aAeb6053F3E94C9b9A09f33669435E7Ef1BeAed")]
    [InlineData("0xfB6916095ca1df60bB79Ce92cE3Ea74c37c5d359")]
    [InlineData("0xdbF03B407c01E7cD3CBea99509d93f8DDDC8C6FB")]
    [InlineData("0xD1220A0cf47c7B9Be7A2E6BA89F429762e7b9aDb")]
    public void Eip55_checksums_match_the_eip_examples(string checksummed)
    {
        Assert.Equal(checksummed, Eip55.ToChecksumAddress(Address(checksummed.ToLowerInvariant())));
    }

    [Fact]
    public void Recovers_the_signer_of_a_published_personal_sign_vector()
    {
        // web3.js accounts.sign("Some data", "0x4c0883a6...") documentation example.
        const string signature = "0xb91467e570a6466aa9e9876cbcd013baba02900b8979d43fe208a4a4f339f5fd6007e74cd82e037b800186422fc2da167c747ef045e5d18a5f5d4300f8e1a0291c";

        Assert.True(EthereumSignature.TryRecoverPersonalSigner("Some data", signature, out var signer));
        Assert.Equal("0x2c7536E3605D9C16a7a3D7b1898e529396a65c23", Eip55.ToChecksumAddress(signer.Value));
        Assert.Equal(new TestWallet("4c0883a69102937d6231471b5dbb6204fe5129617082792ae468d01a3f362318").Address, signer);
    }

    [Fact]
    public void Random_wallets_round_trip_including_multiline_unicode_messages()
    {
        for (var i = 0; i < 25; i++)
        {
            var wallet = TestWallet.Random();
            var message = $"localhost wants you to sign in\nNonce: {i}\nΧαίρετε 🐸";

            Assert.True(EthereumSignature.TryRecoverPersonalSigner(message, wallet.SignPersonal(message), out var signer));
            Assert.Equal(wallet.Address, signer);
        }
    }

    [Fact]
    public void A_signature_over_a_different_message_recovers_a_different_signer()
    {
        var wallet = TestWallet.Random();
        var signature = wallet.SignPersonal("message A");

        var recovered = EthereumSignature.TryRecoverPersonalSigner("message B", signature, out var signer);

        Assert.False(recovered && signer == wallet.Address);
    }

    [Fact]
    public void Accepts_recovery_ids_0_and_1_as_well_as_27_and_28()
    {
        var wallet = TestWallet.Random();
        var signature = wallet.SignPersonal("hello");
        var v = Convert.ToByte(signature[^2..], 16);
        var legacy = signature[..^2] + (v - 27).ToString("x2");

        Assert.True(EthereumSignature.TryRecoverPersonalSigner("hello", legacy, out var signer));
        Assert.Equal(wallet.Address, signer);
    }

    [Fact]
    public void Rejects_the_high_s_twin_of_a_valid_signature()
    {
        var wallet = TestWallet.Random();
        var signature = wallet.SignPersonal("hello");

        Assert.False(EthereumSignature.TryRecoverPersonalSigner("hello", TestWallet.ToHighS(signature), out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0x")]
    [InlineData("0x1234")]
    [InlineData("b91467e570a6466aa9e9876cbcd013baba02900b8979d43fe208a4a4f339f5fd6007e74cd82e037b800186422fc2da167c747ef045e5d18a5f5d4300f8e1a0291c00")]
    [InlineData("0xzz1467e570a6466aa9e9876cbcd013baba02900b8979d43fe208a4a4f339f5fd6007e74cd82e037b800186422fc2da167c747ef045e5d18a5f5d4300f8e1a0291c")]
    [InlineData("0xb91467e570a6466aa9e9876cbcd013baba02900b8979d43fe208a4a4f339f5fd6007e74cd82e037b800186422fc2da167c747ef045e5d18a5f5d4300f8e1a02905")]
    [InlineData("0x00000000000000000000000000000000000000000000000000000000000000006007e74cd82e037b800186422fc2da167c747ef045e5d18a5f5d4300f8e1a0291c")]
    [InlineData("0xb91467e570a6466aa9e9876cbcd013baba02900b8979d43fe208a4a4f339f5fd00000000000000000000000000000000000000000000000000000000000000001c")]
    [InlineData("0xfffffffffffffffffffffffffffffffebaaedce6af48a03bbfd25e8cd03641416007e74cd82e037b800186422fc2da167c747ef045e5d18a5f5d4300f8e1a0291c")]
    public void Rejects_malformed_signatures(string? signature)
    {
        Assert.False(EthereumSignature.TryRecoverPersonalSigner("Some data", signature, out _));
    }

    [Theory]
    [InlineData("0x5aaeb6053f3e94c9b9a09f33669435e7ef1beaed", true)]
    [InlineData("0X5AAEB6053F3E94C9B9A09F33669435E7EF1BEAED", true)]
    [InlineData("5aaeb6053f3e94c9b9a09f33669435e7ef1beaed", false)]
    [InlineData("0x5aaeb6053f3e94c9b9a09f33669435e7ef1beae", false)]
    [InlineData("0x5aaeb6053f3e94c9b9a09f33669435e7ef1beaedd", false)]
    [InlineData("0x5aaeb6053f3e94c9b9a09f33669435e7ef1beaeg", false)]
    [InlineData(null, false)]
    public void Wallet_addresses_are_validated_and_lowercased(string? input, bool valid)
    {
        Assert.Equal(valid, WalletAddress.TryParse(input, out var address));
        if (valid)
        {
            Assert.Equal(input!.ToLowerInvariant().Replace("0x", string.Empty), address.Value[2..]);
            Assert.StartsWith("0x", address.Value);
        }
    }
}
