using ChogGames.Core.Games;
using ChogGames.Core.Rooms;

namespace ChogGames.Games.ChogRoyale.Rules;

/// <summary>
/// A version of the Chog Royale rules (STORY-071, ROYALE-001–004, 010–012, 053–056, 059, DEC-082): how many rounds a match
/// plays, each round's phases, the fictional MON every player starts with and the most a balance may hold, the match's
/// time cap and, since STORY-073 (ROYALE-015–020, DEC-084), the weighted deck and the hand size. Like the other games'
/// rules (DEC-037, DEC-054) it is versioned code in the game module, checked when it is built, so invalid rules stop the
/// API at startup. A match records the version it started with. Nothing here touches a wallet or the chain (ROYALE-012).
/// </summary>
public sealed class RoyaleRules
{
    public const int MaxRounds = 10;

    /// <summary>The most MON any rules version may start a player with, or let a balance reach (ROYALE-014).</summary>
    public const int MonLimit = 10_000;

    public const int HandLimit = 5;

    public const int MaxWeight = 100;

    /// <summary>The longest any phase of a round may last.</summary>
    public static readonly TimeSpan MaxPhase = TimeSpan.FromMinutes(1);

    /// <summary>The shortest decision window, so a player has time to choose.</summary>
    public static readonly TimeSpan MinDecision = TimeSpan.FromSeconds(5);

    private RoyaleRules(
        int version,
        int rounds,
        RoyaleTiming timing,
        TimeSpan timeCap,
        int startingMon,
        int maxMon,
        IReadOnlyDictionary<RoyaleCard, int> deck,
        int handSize)
    {
        Version = version;
        Rounds = rounds;
        Timing = timing;
        TimeCap = timeCap;
        StartingMon = startingMon;
        MaxMon = maxMon;
        Deck = deck;
        HandSize = handSize;
        TotalWeight = deck.Values.Sum();
    }

    /// <summary>The game these rules are for.</summary>
    public string GameType => GameDefinition.ChogRoyale;

    /// <summary>The rules' version, from 1. Any change to the rules bumps it.</summary>
    public int Version { get; }

    /// <summary>Rounds in a match; it ends after the last (ROYALE-002).</summary>
    public int Rounds { get; }

    public RoyaleTiming Timing { get; }

    /// <summary>The longest a match may last (ENG-011); the match ends when it's up, ranked as it stands.</summary>
    public TimeSpan TimeCap { get; }

    /// <summary>Every player's fictional MON at the start (ROYALE-010).</summary>
    public int StartingMon { get; }

    /// <summary>The most MON a balance may hold; every balance stays between 0 and this (ROYALE-011, ROYALE-014).</summary>
    public int MaxMon { get; }

    /// <summary>Each card's weight: a dealt card is that card with its weight's share of the total (ROYALE-015).</summary>
    public IReadOnlyDictionary<RoyaleCard, int> Deck { get; }

    public int TotalWeight { get; }

    /// <summary>Cards each player holds; a played card is replaced after the round, except after the last (ROYALE-016).</summary>
    public int HandSize { get; }

    /// <summary>The rules, checked; any inconsistency throws <see cref="ArgumentException"/> (DEC-082).</summary>
    public static RoyaleRules Create(
        int version,
        int rounds,
        RoyaleTiming timing,
        TimeSpan timeCap,
        int startingMon,
        int maxMon,
        IReadOnlyDictionary<RoyaleCard, int> deck,
        int handSize)
    {
        ArgumentNullException.ThrowIfNull(timing);
        ArgumentNullException.ThrowIfNull(deck);
        if (version < 1)
        {
            throw new ArgumentException("A rules version counts from 1.", nameof(version));
        }

        if (rounds is < 1 or > MaxRounds)
        {
            throw new ArgumentException($"A match plays 1 to {MaxRounds} rounds.", nameof(rounds));
        }

        if (timing.Decision < MinDecision
            || new[] { timing.Announcement, timing.Decision, timing.Reveal, timing.Transition }.Any(phase => phase < TimeSpan.Zero || phase > MaxPhase))
        {
            throw new ArgumentException($"Each phase lasts 0 to {MaxPhase}, and deciding at least {MinDecision}.", nameof(timing));
        }

        if (timeCap > GameRoom.MaxMatchDuration || timeCap < timing.Round * rounds)
        {
            throw new ArgumentException(
                $"The time cap fits every round ({timing.Round * rounds}) and is at most {GameRoom.MaxMatchDuration}.", nameof(timeCap));
        }

        if (startingMon is < 1 or > MonLimit || maxMon < startingMon || maxMon > MonLimit)
        {
            throw new ArgumentException($"Players start with 1 to {MonLimit} MON, and balances may reach at least that, at most {MonLimit}.", nameof(maxMon));
        }

        if (deck.Count != Enum.GetValues<RoyaleCard>().Length || deck.Any(card => !Enum.IsDefined(card.Key) || card.Value is < 1 or > MaxWeight))
        {
            throw new ArgumentException($"The deck weighs every card once, from 1 to {MaxWeight}.", nameof(deck));
        }

        if (handSize is < 1 or > HandLimit)
        {
            throw new ArgumentException($"A hand holds 1 to {HandLimit} cards.", nameof(handSize));
        }

        return new RoyaleRules(
            version, rounds, timing, timeCap, startingMon, maxMon, new Dictionary<RoyaleCard, int>(deck), handSize);
    }

    /// <summary>
    /// The card a weighted draw of <paramref name="roll"/>, from 0 to <see cref="TotalWeight"/> − 1, deals: the cards
    /// take their weights in card order.
    /// </summary>
    public RoyaleCard CardAt(int roll)
    {
        if (roll < 0 || roll >= TotalWeight)
        {
            throw new ArgumentOutOfRangeException(nameof(roll), roll, "A draw falls within the deck's total weight.");
        }

        foreach (var card in Enum.GetValues<RoyaleCard>())
        {
            roll -= Deck[card];
            if (roll < 0)
            {
                return card;
            }
        }

        throw new InvalidOperationException("Unreachable: the weights add up to the total.");
    }

    /// <summary>A balance kept between 0 and <see cref="MaxMon"/> (ROYALE-011).</summary>
    public int Clamp(long mon) => (int)Math.Clamp(mon, 0, MaxMon);
}
