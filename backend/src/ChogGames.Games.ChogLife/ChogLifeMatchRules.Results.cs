using System.Text.Json.Serialization;
using ChogGames.Games.ChogLife.Rules;

namespace ChogGames.Games.ChogLife;

/// <summary>
/// Results and statistics (STORY-043, LIFE-010, DEC-046): every roll tallies its player's statistics on their pawn in the
/// stored state, and when the match ends each player still playing gets a result built from them, stored by the engine
/// with their rank, so it is the same for every player and after any reload.
/// </summary>
public sealed partial class ChogLifeMatchRules
{
    /// <summary>The version of the Degen Score formula, stored with every score so a later formula isn't mixed with it.</summary>
    public const int DegenScoreVersion = 1;

    private const int DegenRoutePoints = 30;
    private const int RiskEventPoints = 10;
    private const int DegenWinPoints = 10;
    private const int MaxDegenScore = 100;

    /// <summary>
    /// The Degen Score (version 1): how much risk a player took, not how well they did. 30 points per Degen route taken,
    /// 10 per Rug or Liquidation landed on and 10 per Degen stake that paid back more than it staked, at most 100. It
    /// doesn't count towards the ranking.
    /// </summary>
    private static int DegenScore(LifeStats stats) => (int)Math.Min(
        MaxDegenScore,
        ((long)stats.DegenRoutes * DegenRoutePoints)
        + ((long)(stats.Rugs + stats.Liquidations) * RiskEventPoints)
        + ((long)stats.DegenWins * DegenWinPoints));

    /// <summary>
    /// <paramref name="stats"/> after <paramref name="move"/>, which changed its player's total portfolio by
    /// <paramref name="gained"/> MON (stake payout, tile, ability and finish bonus together): the Degen route it left a
    /// fork by, the Degen stake it cashed with a profit, the Airdrop, Rug or Liquidation it landed on, and the biggest
    /// win, the most a single roll has added. A pass on the Finish changes nothing. All zeros are stored as null.
    /// </summary>
    private static LifeStats? Tallied(LifeStats? stats, LifeMove move, int gained, LifeRules rules)
    {
        var tally = stats ?? new LifeStats();
        if (move.Roll is null)
        {
            return stats;
        }

        var leftByDegen = rules.Tiles[move.From].Type == LifeTileType.Branch
            && move.Path.Count > 0
            && rules.Tiles[move.Path[0]].Lane == LifeLane.Degen;
        var degenWin = move.Matured is { Lane: LifeLane.Degen } matured && matured.Payout > matured.Stake;
        var landed = rules.Tiles[move.To] is { Type: LifeTileType.Event } tile ? tile.Event : null;
        var tallied = tally with
        {
            DegenRoutes = tally.DegenRoutes + (leftByDegen ? 1 : 0),
            DegenWins = tally.DegenWins + (degenWin ? 1 : 0),
            Airdrops = tally.Airdrops + (landed == LifeEventKind.Airdrop ? 1 : 0),
            AirdropMon = landed == LifeEventKind.Airdrop && move.Change > 0
                ? (int)Math.Min((long)tally.AirdropMon + move.Change.Value, LifeRules.MaxMon)
                : tally.AirdropMon,
            Rugs = tally.Rugs + (landed == LifeEventKind.Rug ? 1 : 0),
            Liquidations = tally.Liquidations + (landed == LifeEventKind.Liquidation ? 1 : 0),
            BiggestWin = Math.Max(tally.BiggestWin, gained),
        };
        return tallied == new LifeStats() ? null : tallied;
    }

    /// <summary>A player's result: their final portfolio, place at the Finish and statistics (LIFE-010, DEC-046).</summary>
    private static LifeResult ResultOf(LifePawn pawn, int? place, LifeRules rules)
    {
        var portfolio = PortfolioOf(pawn);
        var stats = pawn.Stats ?? new LifeStats();
        return new LifeResult(
            rules.StartingMon,
            portfolio.Total,
            portfolio.Liquid,
            portfolio.Assets,
            portfolio.Bonuses,
            portfolio.Liabilities,
            place,
            stats.Airdrops,
            stats.AirdropMon,
            stats.Rugs,
            stats.Liquidations,
            stats.BiggestWin,
            stats.DegenRoutes,
            stats.DegenWins,
            DegenScore(stats),
            DegenScoreVersion);
    }

    /// <summary>
    /// A player's statistics so far, kept on their pawn (null in states from before STORY-043, read as all zeros; zero counts aren't stored): the
    /// Airdrops landed on and the MON they paid, the Rugs and Liquidations landed on (and survived: nobody is knocked
    /// out), the biggest win, the Degen routes taken and the Degen stakes that paid back more than they staked.
    /// </summary>
    private sealed record LifeStats(
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] int Airdrops = 0,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] int AirdropMon = 0,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] int Rugs = 0,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] int Liquidations = 0,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] int BiggestWin = 0,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] int DegenRoutes = 0,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] int DegenWins = 0)
    {
        public bool IsValid() => new[] { Airdrops, AirdropMon, Rugs, Liquidations, BiggestWin, DegenRoutes, DegenWins }
            .All(count => count is >= 0 and <= LifeRules.MaxMon);
    }

    /// <summary>
    /// A player's result, stored with their rank when the match ends: the starting and final total portfolio with its
    /// parts, their <paramref name="Place"/> at the Finish (null if they didn't reach it), their statistics and their
    /// Degen Score with the formula version it was computed with.
    /// </summary>
    private sealed record LifeResult(
        int StartingPortfolio,
        int Portfolio,
        int Liquid,
        int Assets,
        int Bonuses,
        int Liabilities,
        int? Place,
        int Airdrops,
        int AirdropMon,
        int RugsSurvived,
        int Liquidations,
        int BiggestWin,
        int DegenRoutes,
        int DegenWins,
        int DegenScore,
        int DegenScoreVersion);
}
