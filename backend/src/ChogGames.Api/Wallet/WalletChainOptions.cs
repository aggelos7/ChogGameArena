namespace ChogGames.Api.Wallet;

/// <summary>
/// The one EVM network wallets must use, bound from <c>Wallet:Chain</c>. Public network parameters only:
/// they are sent to browsers, so never put a keyed (secret) RPC URL here.
/// </summary>
public sealed class WalletChainOptions
{
    public const string SectionName = "Wallet:Chain";

    /// <summary>EIP-155 chain id, e.g. 143 (Monad Mainnet) or 10143 (Monad Testnet).</summary>
    public long ChainId { get; set; }

    public string Name { get; set; } = string.Empty;

    public string CurrencyName { get; set; } = string.Empty;

    public string CurrencySymbol { get; set; } = string.Empty;

    public int CurrencyDecimals { get; set; } = 18;

    /// <summary>Public HTTPS RPC endpoints offered to wallets that do not know the chain yet.</summary>
    public List<string> RpcUrls { get; set; } = [];

    public List<string> BlockExplorerUrls { get; set; } = [];
}
