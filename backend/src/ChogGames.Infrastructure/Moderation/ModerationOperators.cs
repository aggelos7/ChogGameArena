using ChogGames.Core.Players;
using Microsoft.Extensions.Options;

namespace ChogGames.Infrastructure.Moderation;

/// <summary>
/// Who reviews photo reports: players signed in with one of <see cref="ModerationOptions.OperatorWallets"/> (STORY-069,
/// DEC-070). Guests never are; a wallet only proves itself by a verified sign-in.
/// </summary>
public sealed class ModerationOperators(IOptions<ModerationOptions> options)
{
    private readonly IReadOnlySet<string> _wallets = options.Value.ParseOperatorWallets()
        ?? throw new InvalidOperationException("Moderation:OperatorWallets must list wallet addresses.");

    public bool Includes(Player player) => player.WalletAddress is { } wallet && _wallets.Contains(wallet);
}
