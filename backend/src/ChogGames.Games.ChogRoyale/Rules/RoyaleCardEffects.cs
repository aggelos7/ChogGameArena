namespace ChogGames.Games.ChogRoyale.Rules;

/// <summary>
/// What the cards do, in whole MON (STORY-075, STORY-076, ROYALE-025, DEC-086, DEC-087).
/// </summary>
/// <param name="Pump">Pump's gain before market modifiers.</param>
/// <param name="RugPull">The most a Rug Pull steals, never more than the victim holds.</param>
/// <param name="DiamondHands">What Diamond Hands gains its player, besides its shield.</param>
/// <param name="SwapPercent">The share of each side's pre-round balance a Whale Swap exchanges, rounded down.</param>
/// <param name="SwapCap">The most MON a Whale Swap moves each way.</param>
/// <param name="AirdropMin">Airdrop's smallest gain.</param>
/// <param name="AirdropMax">Airdrop's largest gain.</param>
public sealed record RoyaleCardEffects(
    int Pump, int RugPull, int DiamondHands, int SwapPercent, int SwapCap, int AirdropMin, int AirdropMax)
{
    /// <summary>The most MON any one card effect or market amount may name.</summary>
    public const int Limit = 100;

    /// <summary>What one side of a Whale Swap gives, from its pre-round balance: the share, rounded down, within the cap.</summary>
    public int SwapAmount(int snapshot) => Math.Min(SwapCap, (int)((long)snapshot * SwapPercent / 100));

    internal void Check()
    {
        if (new[] { Pump, RugPull, SwapCap, AirdropMin }.Any(value => value is < 1 or > Limit)
            || DiamondHands is < 0 or > Limit
            || SwapPercent is < 1 or > 100
            || AirdropMax < AirdropMin
            || AirdropMax > Limit)
        {
            throw new ArgumentException(
                $"Card effects gain or move 1 to {Limit} MON (Diamond Hands from 0), a swap 1 to 100 percent, and Airdrop's range is ordered.",
                "effects");
        }
    }
}
