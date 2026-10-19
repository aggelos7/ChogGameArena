using ChogGames.Core.Wallets;

namespace ChogGames.Infrastructure.Moderation;

/// <summary>Photo reports and their review (<c>Moderation</c>, STORY-069, DEC-070).</summary>
public sealed class ModerationOptions
{
    public const string SectionName = "Moderation";

    /// <summary>
    /// Wallet addresses of the operators who review reports, separated by commas, spaces or semicolons. Empty means no
    /// one can review, which is the default.
    /// </summary>
    public string OperatorWallets { get; set; } = string.Empty;

    /// <summary>Different players who must report a photo before it is hidden from everyone but its owner.</summary>
    public int HideAfterReporters { get; set; } = 3;

    /// <summary>Reports a player may send per <see cref="Window"/>, refused ones included.</summary>
    public int ReportsPerWindowPerPlayer { get; set; } = 10;

    public TimeSpan Window { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>The operators' addresses, lowercase; null when one of them isn't a wallet address.</summary>
    public IReadOnlySet<string>? ParseOperatorWallets()
    {
        var addresses = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in OperatorWallets.Split([',', ';', ' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (!WalletAddress.TryParse(entry, out var address))
            {
                return null;
            }

            addresses.Add(address.Value);
        }

        return addresses;
    }
}
