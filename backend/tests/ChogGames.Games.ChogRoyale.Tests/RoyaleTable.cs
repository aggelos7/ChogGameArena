using System.Text.Json;
using ChogGames.Core.Games;
using ChogGames.Core.Matches;
using ChogGames.Core.Players;
using ChogGames.Core.Rooms;
using ChogGames.Games.ChogRoyale.Rules;

namespace ChogGames.Games.ChogRoyale.Tests;

/// <summary>A source of chance that returns queued values, each checked against its range, then the lowest allowed.</summary>
public sealed class Draws(params int[] values) : IRandomSource
{
    private readonly Queue<int> _values = new(values);

    public List<(int Min, int Max)> Asked { get; } = [];

    public void Enqueue(params int[] more)
    {
        foreach (var value in more)
        {
            _values.Enqueue(value);
        }
    }

    public int Next(int minInclusive, int maxExclusive)
    {
        Asked.Add((minInclusive, maxExclusive));
        var value = _values.Count > 0 ? _values.Dequeue() : minInclusive;
        Assert.InRange(value, minInclusive, maxExclusive - 1);
        return value;
    }
}

/// <summary>
/// A Chog Royale match played through the shared engine's round mode, as the API plays it: submissions go through the
/// rules and <see cref="GameRoom.ApplySubmission"/>, rounds through the rules and <see cref="GameRoom.ResolveRound"/>.
/// Players are seated in the order given.
/// </summary>
public sealed class RoyaleTable
{
    public static readonly DateTimeOffset Start = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

    private static readonly Guid MatchId = Guid.NewGuid();

    public RoyaleTable(int players, IRandomSource? random = null, ChogRoyaleMatchRules? rules = null)
    {
        Rules = rules ?? new ChogRoyaleMatchRules([RoyaleRulesV1.Rules]);
        Random = random ?? new Draws();
        Players = [.. Enumerable.Range(1, players).Select(_ => Guest())];
        Assert.True(RoomCode.TryParse("K7QF3M", out var code));
        Match = GameRoom.Create(GameDefinition.Create(GameDefinition.ChogRoyale, 2, 6), Players[0], code, Start);
        for (var i = 1; i < players; i++)
        {
            Match.Join(Players[i], Start.AddSeconds(i));
        }

        foreach (var player in Players)
        {
            Match.MarkReady(player);
        }

        Match.Start(
            Players[0].Id,
            Players,
            Players.ToDictionary(p => p.Id, _ => CharacterSnapshot.Standard(DefaultAvatar.Classic, Start)),
            TimeSpan.FromSeconds(25),
            new KeepOrder(),
            Start,
            startState: room => Rules.StartState(new MatchStartContext(room, Random))!.Value.GetRawText(),
            timeLimit: Rules.TimeLimit,
            rounds: Rules.RoundTiming);
        Now = Match.RoundOpensAt!.Value;
    }

    public ChogRoyaleMatchRules Rules { get; }

    public IRandomSource Random { get; }

    public Player[] Players { get; }

    public GameRoom Match { get; }

    public DateTimeOffset Now { get; private set; }

    public JsonElement State => JsonDocument.Parse(Match.State!).RootElement;

    public Guid Seat(int seat) => Match.Members.Single(m => m.Seat == seat).PlayerId;

    /// <summary>Sends <c>SUBMIT_ACTION</c> for the player in <paramref name="seat"/>; an accepted one is applied.</summary>
    public MatchRuleDecision Submit(int seat, object payload)
    {
        var playerId = Seat(seat);
        var command = new MatchCommand(Guid.NewGuid(), Match.Id, Match.Version, ChogRoyaleMatchRules.SubmitAction, JsonSerializer.SerializeToElement(payload));
        var problem = Match.CheckCommand(playerId, command, Now);
        Assert.Null(problem);
        var decision = Rules.Apply(new MatchCommandContext(Match, playerId, command.Type, command.Payload, State, Random));
        if (decision.Outcome == MatchRuleOutcome.Accepted)
        {
            Match.ApplySubmission(playerId, command, decision.State!.Value.GetRawText(), decision.Locks, Now);
        }

        return decision;
    }

    /// <summary>Plays the card at <paramref name="index"/> of the seat's hand.</summary>
    public MatchRuleDecision Play(int seat, int index, int? target = null, bool locks = false) =>
        Submit(seat, new { card = Hand(seat)[index].Id, target, @lock = locks });

    public MatchRuleDecision Hodl(int seat, bool locks = false) => Submit(seat, new { hodl = true, @lock = locks });

    /// <summary>The round's deadline passes and it resolves; returns the rules' resolution.</summary>
    public MatchRoundResolution Resolve()
    {
        Now = Match.IsRoundDue(Now) ? Now : Match.TurnEndsAt!.Value;
        var resolution = Rules.ResolveRound(new MatchRoundContext(Match, State, Random));
        Match.ResolveRound(resolution.State.GetRawText(), Now, Rules.RoundTiming.OpensAfter, resolution.Standings);
        if (Match.Status == RoomStatus.Active)
        {
            Now = Match.RoundOpensAt!.Value;
        }

        return resolution;
    }

    public JsonElement ViewOf(int seat) => Rules.ViewFor(new MatchViewContext(Match, Seat(seat), State))!.Value;

    public RoyaleMatchState Stored => State.Deserialize<RoyaleMatchState>(Json)!;

    public RoyaleHandCard[] Hand(int seat) => Stored.Players.Single(p => p.Seat == seat).Hand;

    public int Mon(int seat) => Stored.Players.Single(p => p.Seat == seat).Mon;

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private static Player Guest()
    {
        Assert.True(PlayerDisplayName.TryCreate("Chog", out var name));
        var player = Player.CreateGuest(name, Start);
        player.SelectStandardCharacter(DefaultAvatar.Classic);
        return player;
    }

    /// <summary>Seats in join order: a shuffle that never swaps.</summary>
    private sealed class KeepOrder : IRandomSource
    {
        public int Next(int minInclusive, int maxExclusive) => maxExclusive - 1;
    }
}
