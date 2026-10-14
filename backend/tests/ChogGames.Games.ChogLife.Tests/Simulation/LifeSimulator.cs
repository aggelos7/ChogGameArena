using System.Text.Json;
using ChogGames.Core.Abilities;
using ChogGames.Core.Games;
using ChogGames.Core.Matches;
using ChogGames.Core.Players;
using ChogGames.Core.Rooms;
using ChogGames.Games.ChogLife.Abilities;
using ChogGames.Games.ChogLife.Rules;

namespace ChogGames.Games.ChogLife.Tests.Simulation;

/// <summary>How a simulated player picks a lane at a fork.</summary>
public enum LaneStrategy
{
    Safe,
    Degen,
    Coin,
}

/// <summary>
/// A simulated player: how they choose lanes, the ability they lock (null for none), the chance they let any one turn run
/// out (AFK), a window in which they are disconnected and act on nothing, and when they leave the match, if they do.
/// </summary>
public sealed record SimPlayer(
    LaneStrategy Lanes,
    string? Ability = null,
    double AfkChance = 0,
    (TimeSpan From, TimeSpan To)? Offline = null,
    TimeSpan? LeavesAt = null);

/// <summary>
/// The pace of a simulated match: each action a player takes comes after a think time drawn between
/// <paramref name="MinThink"/> and <paramref name="MaxThink"/>; a think time past the turn's end lets the turn run out.
/// </summary>
public sealed record SimPace(TimeSpan MinThink, TimeSpan MaxThink, TimeSpan TurnTimeout, int MaxMissedTurns = 3)
{
    /// <summary>The server defaults (DEC-027, DEC-045) with players taking 2–8 seconds an action.</summary>
    public static SimPace Engaged { get; } = new(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(25));
}

public enum SimEnding
{
    Finish,
    TimeUp,
    Abandoned,
}

/// <param name="Rank">Null for a player who left, or in an abandoned match.</param>
/// <param name="Result">The game's stored result (DEC-046); null for a player who left, or in an abandoned match.</param>
public sealed record SimPlayerResult(SimPlayer Player, int Seat, int? Rank, JsonElement? Result, bool Left)
{
    public int Portfolio => Result?.GetProperty("portfolio").GetInt32() ?? 0;

    public int? Place => Result?.TryGetProperty("place", out var place) == true ? place.GetInt32() : null;
}

/// <summary>
/// One simulated match: how it ended, how long it lasted on the server's clock, the turns played and timed out, the
/// commands and ability uses accepted, and each player's outcome by seat.
/// </summary>
public sealed record SimMatch(
    int Seed,
    SimEnding Ending,
    TimeSpan Duration,
    int Turns,
    int Commands,
    int AbilityUses,
    int TimedOutTurns,
    IReadOnlyList<SimPlayerResult> Players);

/// <summary>
/// STORY-044: plays whole Chog Life matches with the real engine (<see cref="GameRoom"/>), rules
/// (<see cref="ChogLifeMatchRules"/>) and abilities (<see cref="AbilityEngine"/>) on a virtual clock, as the API and the
/// turn timer would: each action is checked by the engine, decided by the rules and applied to the room; a turn nobody
/// plays runs out with the rules' safe default; the match ends with the rules' standings when everyone has finished or
/// its time is up. Every draw comes from a source seeded with <c>seed</c>, so a match replays exactly.
/// The simulated players only send commands the game allows at that moment, so any refusal is a defect, and every
/// step must change the room: a step that doesn't is a stuck turn, which throws.
/// </summary>
public sealed class LifeSimulator
{
    /// <summary>More steps than any match can take within its time cap; reaching it means the match is stuck.</summary>
    public const int MaxSteps = 5_000;

    private static readonly DateTimeOffset Epoch = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    private readonly ChogLifeMatchRules _rules;
    private readonly AbilityEngine _abilities;
    private readonly LifeRules _life;

    /// <param name="rules">The rules version to play; new matches' (version 2, DEC-047) by default.</param>
    public LifeSimulator(LifeRules? rules = null)
    {
        _life = rules ?? LifeRulesV2.Rules;
        _rules = new ChogLifeMatchRules([_life]);
        _abilities = new AbilityEngine([LifeAbilities.Catalog(_rules)]);
    }

    /// <summary>A source of chance seeded for one match; the server's is <c>SecureRandomSource</c>.</summary>
    private sealed class SeededRandom(int seed) : IRandomSource
    {
        private readonly Random _random = new(seed);

        public int Next(int minInclusive, int maxExclusive) => _random.Next(minInclusive, maxExclusive);
    }

    public SimMatch Play(int seed, IReadOnlyList<SimPlayer> profiles, SimPace? pace = null)
    {
        pace ??= SimPace.Engaged;
        var dice = new SeededRandom(seed);
        var behaviour = new Random(unchecked((seed * 7919) + 17));
        var (room, players) = Start(profiles, pace, dice);
        var profileOf = players.Select((player, i) => (player.Id, Profile: profiles[i])).ToDictionary(p => p.Id, p => p.Profile);

        var now = room.StartedAt!.Value;
        var commands = 0;
        var abilityUses = 0;
        var timedOut = 0;
        var afkTurn = (Turn: -1, Afk: false);
        for (var step = 0; room.Status == RoomStatus.Active; step++)
        {
            if (step == MaxSteps)
            {
                throw new InvalidOperationException($"Seed {seed}: the match is stuck after {MaxSteps} steps.");
            }

            var version = room.Version;
            Invariants(room, now, seed);

            if (players.FirstOrDefault(p => profileOf[p.Id].LeavesAt is { } at && now - room.StartedAt >= at && room.IsPlaying(p.Id)) is { } leaver)
            {
                room.Leave(leaver.Id, now);
            }
            else if (room.IsDueToEnd(now))
            {
                End(room, now);
            }
            else
            {
                var current = room.CurrentPlayerId!.Value;
                var profile = profileOf[current];
                if (afkTurn.Turn != room.TurnNumber)
                {
                    afkTurn = (room.TurnNumber, behaviour.NextDouble() < profile.AfkChance);
                }

                var actsAt = now + Think(pace, behaviour);
                var online = profile.Offline is not { } offline
                    || actsAt - room.StartedAt < offline.From
                    || actsAt - room.StartedAt >= offline.To;
                if (!afkTurn.Afk && online && actsAt < room.TurnEndsAt && actsAt < room.MatchEndsAt)
                {
                    now = actsAt;
                    var used = Act(room, current, profile, dice, behaviour, now, seed);
                    commands += used ? 0 : 1;
                    abilityUses += used ? 1 : 0;
                }
                else
                {
                    // Nobody acts before the turn or the match runs out: the turn timer (or the match's end) takes over.
                    now = room.TurnEndsAt!.Value < room.MatchEndsAt!.Value ? room.TurnEndsAt.Value : room.MatchEndsAt.Value;
                    if (!room.IsDueToEnd(now))
                    {
                        TimeOut(room, dice, pace, now);
                        timedOut++;
                    }
                }
            }

            if (room.Version == version && room.Status == RoomStatus.Active && !room.IsDueToEnd(now))
            {
                throw new InvalidOperationException($"Seed {seed}: step {step} changed nothing.");
            }
        }

        var ending = room.Status switch
        {
            RoomStatus.Abandoned => SimEnding.Abandoned,
            RoomStatus.Completed when room.EndedAt >= room.MatchEndsAt => SimEnding.TimeUp,
            RoomStatus.Completed => SimEnding.Finish,
            _ => throw new InvalidOperationException($"Seed {seed}: the match stopped as {room.Status}."),
        };
        return new SimMatch(
            seed,
            ending,
            room.EndedAt!.Value - room.StartedAt!.Value,
            room.TurnNumber,
            commands,
            abilityUses,
            timedOut,
            players.Select(player =>
            {
                var member = room.Members.Single(m => m.PlayerId == player.Id);
                return new SimPlayerResult(
                    profileOf[player.Id],
                    member.Seat!.Value,
                    member.HasLeft || ending == SimEnding.Abandoned ? null : member.Rank,
                    member.Result is { } result ? JsonDocument.Parse(result).RootElement.Clone() : null,
                    member.HasLeft);
            }).ToArray());
    }

    private (GameRoom Room, Player[] Players) Start(IReadOnlyList<SimPlayer> profiles, SimPace pace, IRandomSource dice)
    {
        var players = profiles.Select((_, i) =>
        {
            if (!PlayerDisplayName.TryCreate($"Player {i + 1}", out var name))
            {
                throw new InvalidOperationException("Bad display name.");
            }

            return Player.CreateGuest(name, Epoch);
        }).ToArray();
        if (!RoomCode.TryParse("K7QF3M", out var code))
        {
            throw new InvalidOperationException("Bad room code.");
        }

        var room = GameRoom.Create(GameDefinition.Create(GameDefinition.ChogLife, 2, 6), players[0], code, Epoch);
        for (var i = 1; i < players.Length; i++)
        {
            room.Join(players[i], Epoch.AddSeconds(i));
        }

        var catalog = _abilities.CatalogFor(GameDefinition.ChogLife)!;
        var locked = new Dictionary<Guid, AbilitySnapshot>();
        for (var i = 0; i < players.Length; i++)
        {
            if (profiles[i].Ability is { } ability)
            {
                room.SelectAbility(players[i].Id, ability);
                locked[players[i].Id] = AbilitySnapshot.Lock(catalog, catalog.Definitions.Single(d => d.Id == ability));
            }

            room.MarkReady(players[i]);
        }

        var startedAt = Epoch.AddMinutes(1);
        room.Start(
            players[0].Id,
            players,
            players.ToDictionary(p => p.Id, _ => CharacterSnapshot.Standard(DefaultAvatar.Classic, startedAt)),
            pace.TurnTimeout,
            dice,
            startedAt,
            locked,
            started => _rules.StartState(new MatchStartContext(started, dice))?.GetRawText(),
            _rules.TimeLimit);
        return (room, players);
    }

    private static TimeSpan Think(SimPace pace, Random behaviour) =>
        pace.MinThink + ((pace.MaxThink - pace.MinThink) * behaviour.NextDouble());

    /// <summary>
    /// What the engine guarantees between steps: the turn belongs to a player still playing and not finished, it never
    /// runs past the match's end by more than one turn, and the match never outlives its cap.
    /// </summary>
    private static void Invariants(GameRoom room, DateTimeOffset now, int seed)
    {
        var current = room.Members.Single(m => m.PlayerId == room.CurrentPlayerId);
        if (current.HasLeft || (current.HasFinished && !room.IsDueToEnd(now)))
        {
            throw new InvalidOperationException($"Seed {seed}: the turn belongs to a player who can't take it.");
        }

        if (now > room.MatchEndsAt || now - room.StartedAt > GameRoom.MaxMatchDuration)
        {
            throw new InvalidOperationException($"Seed {seed}: the match ran past its cap.");
        }
    }

    /// <summary>One action by the current player; returns whether it was an ability use.</summary>
    private bool Act(GameRoom room, Guid player, SimPlayer profile, IRandomSource dice, Random behaviour, DateTimeOffset now, int seed)
    {
        using var document = JsonDocument.Parse(room.State!);
        var state = document.RootElement;
        var seat = room.Members.Single(m => m.PlayerId == player).Seat!.Value;
        var pawn = state.GetProperty("pawns").EnumerateArray().Single(p => p.GetProperty("seat").GetInt32() == seat);
        var choosing = state.TryGetProperty("choosing", out var c) && c.GetInt32() == seat;
        var confirming = state.TryGetProperty("confirming", out var d) && d.GetInt32() == seat;
        var armed = pawn.TryGetProperty("armed", out _);
        var usesLeft = room.Members.Single(m => m.PlayerId == player).AbilityUsesLeft ?? 0;

        if (confirming)
        {
            var lane = Enum.Parse<LifeLane>(pawn.GetProperty("lane").GetString()!);
            Command(room, player, ChogLifeMatchRules.ChooseLane, new { lane = KeepOrSwitch(room, player, lane) }, dice, now, seed);
            return false;
        }

        if (usesLeft > 0 && !armed && (profile.Ability == LifeAbilities.EventPreview) == choosing)
        {
            UseAbility(room, player, profile.Ability!, dice, now, seed);
            return true;
        }

        if (choosing)
        {
            Command(room, player, ChogLifeMatchRules.ChooseLane, new { lane = LaneFor(profile, behaviour).ToString() }, dice, now, seed);
            return false;
        }

        Command(room, player, ChogLifeMatchRules.Roll, new { }, dice, now, seed);
        return false;
    }

    /// <summary>A player's coin flip comes from their behaviour, never from the server's chance.</summary>
    private static LifeLane LaneFor(SimPlayer profile, Random behaviour) => profile.Lanes switch
    {
        LaneStrategy.Safe => LifeLane.Safe,
        LaneStrategy.Degen => LifeLane.Degen,
        _ => behaviour.Next(2) == 0 ? LifeLane.Safe : LifeLane.Degen,
    };

    /// <summary>After an Event Preview: switch lanes when the previewed move lands on a loss, else keep it.</summary>
    private string KeepOrSwitch(GameRoom room, Guid player, LifeLane lane)
    {
        using var document = JsonDocument.Parse(room.State!);
        var view = _rules.ViewFor(new MatchViewContext(room, player, document.RootElement))!.Value;
        var preview = view.GetProperty("preview");
        var tile = _life.Tiles[preview.GetProperty("to").GetInt32()];
        var loses = tile.Type == LifeTileType.Loss
            || (preview.TryGetProperty("outcome", out var outcome) && outcome.GetProperty("effect").GetString() == nameof(LifeEffect.Loss));
        return (loses ? (lane == LifeLane.Safe ? LifeLane.Degen : LifeLane.Safe) : lane).ToString();
    }

    private void Command(GameRoom room, Guid player, string type, object payload, IRandomSource dice, DateTimeOffset now, int seed)
    {
        var body = JsonSerializer.SerializeToElement(payload);
        var command = new MatchCommand(Guid.NewGuid(), room.Id, room.Version, type, body);
        if (room.CheckCommand(player, command, now) is { } problem)
        {
            throw new InvalidOperationException($"Seed {seed}: the engine refused {type}: {problem}.");
        }

        using var state = JsonDocument.Parse(room.State!);
        var decision = _rules.Apply(new MatchCommandContext(room, player, type, body, state.RootElement, dice));
        if (decision.Outcome != MatchRuleOutcome.Accepted)
        {
            throw new InvalidOperationException($"Seed {seed}: the rules refused {type}: {decision.Reason ?? decision.Outcome.ToString()}.");
        }

        room.ApplyCommand(player, command, decision.State!.Value.GetRawText(), decision.EndsTurn, now, decision.Standings, decision.Finished);
    }

    private void UseAbility(GameRoom room, Guid player, string ability, IRandomSource dice, DateTimeOffset now, int seed)
    {
        var body = JsonSerializer.SerializeToElement(new { abilityId = ability });
        var command = new MatchCommand(Guid.NewGuid(), room.Id, room.Version, MatchCommand.UseAbility, body);
        if (room.CheckCommand(player, command, now) is { } problem)
        {
            throw new InvalidOperationException($"Seed {seed}: the engine refused {ability}: {problem}.");
        }

        using var state = JsonDocument.Parse(room.State!);
        var result = _abilities.Use(new AbilityRequest(room, player, body, state.RootElement, dice));
        if (!result.Used)
        {
            throw new InvalidOperationException($"Seed {seed}: {ability} was refused: {result.Reason}.");
        }

        room.ApplyAbility(player, command, ability, result.State!.Value.GetRawText(), now);
    }

    private void TimeOut(GameRoom room, IRandomSource dice, SimPace pace, DateTimeOffset now)
    {
        using var state = room.State is null ? null : JsonDocument.Parse(room.State);
        var safeDefault = _rules.TimeOutTurn(new MatchTurnContext(room, state?.RootElement, dice));
        room.TimeOutTurn(safeDefault?.GetRawText(), pace.MaxMissedTurns, now);
    }

    private void End(GameRoom room, DateTimeOffset now)
    {
        using var state = room.State is null ? null : JsonDocument.Parse(room.State);
        var ending = _rules.EndMatch(new MatchEndContext(room, state?.RootElement, now >= room.MatchEndsAt));
        room.End(ending.State.GetRawText(), ending.Standings, now);
    }
}
