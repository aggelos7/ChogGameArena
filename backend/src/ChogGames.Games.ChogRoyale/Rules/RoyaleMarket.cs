namespace ChogGames.Games.ChogRoyale.Rules;

/// <summary>
/// The market (STORY-077, ROYALE-021–025, DEC-088): every event's weight for the round's draw, the Chaos pool's weights
/// (STORY-076, ROYALE-024, DEC-087), and what the modifiers do. Amounts round down to whole MON.
/// </summary>
/// <param name="Events">Each event's weight in the round's draw.</param>
/// <param name="Chaos">Each modifier's weight in Chaos's draw; every event but Calm Market, and never the round's own.</param>
/// <param name="HypePercent">Pump's gain under Meme Hype, as a percentage.</param>
/// <param name="CrashPercent">Pump's gain under Market Crash, as a percentage.</param>
/// <param name="WhaleBonus">What Whale Incoming adds to a successful Rug Pull, within the victim's balance.</param>
/// <param name="CommunityAirdrop">What Community Airdrop gives every player after the cards.</param>
public sealed record RoyaleMarket(
    IReadOnlyDictionary<RoyaleMarketEvent, int> Events,
    IReadOnlyDictionary<RoyaleMarketEvent, int> Chaos,
    int HypePercent,
    int CrashPercent,
    int WhaleBonus,
    int CommunityAirdrop)
{
    public const int MaxWeight = 100;

    /// <summary>The highest Pump percentage, so stacked modifiers stay bounded.</summary>
    public const int MaxPercent = 300;

    public int EventWeight => Events.Values.Sum();

    /// <summary>The event a weighted draw of <paramref name="roll"/>, from 0 to <see cref="EventWeight"/> − 1, gives, in event order.</summary>
    public RoyaleMarketEvent EventAt(int roll) => At(Events, roll);

    /// <summary>The Chaos pool's total weight in a round whose event is <paramref name="roundEvent"/>.</summary>
    public int ChaosWeight(RoyaleMarketEvent roundEvent) => ChaosPool(roundEvent).Values.Sum();

    /// <summary>The modifier Chaos adds for a draw of <paramref name="roll"/> in a round whose event is <paramref name="roundEvent"/>.</summary>
    public RoyaleMarketEvent ChaosAt(RoyaleMarketEvent roundEvent, int roll) => At(ChaosPool(roundEvent), roll);

    private Dictionary<RoyaleMarketEvent, int> ChaosPool(RoyaleMarketEvent roundEvent) =>
        Chaos.Where(modifier => modifier.Key != roundEvent).ToDictionary();

    private static RoyaleMarketEvent At(IReadOnlyDictionary<RoyaleMarketEvent, int> weights, int roll)
    {
        if (roll < 0 || roll >= weights.Values.Sum())
        {
            throw new ArgumentOutOfRangeException(nameof(roll), roll, "A draw falls within the pool's total weight.");
        }

        foreach (var marketEvent in Enum.GetValues<RoyaleMarketEvent>().Where(weights.ContainsKey))
        {
            roll -= weights[marketEvent];
            if (roll < 0)
            {
                return marketEvent;
            }
        }

        throw new InvalidOperationException("Unreachable: the weights add up to the total.");
    }

    internal RoyaleMarket Checked()
    {
        var all = Enum.GetValues<RoyaleMarketEvent>();
        if (Events.Count != all.Length || Events.Any(e => !Enum.IsDefined(e.Key) || e.Value is < 1 or > MaxWeight))
        {
            throw new ArgumentException($"The market weighs every event once, from 1 to {MaxWeight}.", "market");
        }

        if (Chaos.Count != all.Length - 1
            || Chaos.Any(e => !Enum.IsDefined(e.Key) || e.Key == RoyaleMarketEvent.CalmMarket || e.Value is < 1 or > MaxWeight))
        {
            throw new ArgumentException($"The Chaos pool weighs every modifier but Calm Market once, from 1 to {MaxWeight}.", "market");
        }

        if (HypePercent is < 100 or > MaxPercent
            || CrashPercent is < 0 or > 100
            || WhaleBonus is < 0 or > RoyaleCardEffects.Limit
            || CommunityAirdrop is < 0 or > RoyaleCardEffects.Limit)
        {
            throw new ArgumentException(
                $"Meme Hype raises Pump to at most {MaxPercent}%, Market Crash lowers it, and the bonuses are 0 to {RoyaleCardEffects.Limit} MON.",
                "market");
        }

        return this with { Events = new Dictionary<RoyaleMarketEvent, int>(Events), Chaos = new Dictionary<RoyaleMarketEvent, int>(Chaos) };
    }
}
