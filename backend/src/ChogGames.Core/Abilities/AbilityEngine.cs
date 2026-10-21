using System.Text.Json;
using ChogGames.Core.Matches;
using ChogGames.Core.Rooms;

namespace ChogGames.Core.Abilities;

/// <summary>
/// Uses abilities by id for the shared engine (STORY-030, NFT-014, DEC-032). It finds the ability in the match's game
/// catalog, checks the command's payload and target, asks the game whether the match is in one of the ability's windows
/// and whether its own conditions hold, and only then lets the handler apply the effect. Every refusal changes nothing
/// and draws nothing. It reads only seats, ids and locked abilities from the match, never characters or NFT traits
/// (ADR-007, NFT-016). A player may only use the ability they locked at start, while it has uses left, with the values
/// locked then; the game's current handler applies it (STORY-034, NFT-015, DEC-036).
/// </summary>
public sealed class AbilityEngine
{
    private readonly Dictionary<string, AbilityCatalog> _catalogs;

    public AbilityEngine(IEnumerable<AbilityCatalog> catalogs)
    {
        _catalogs = new Dictionary<string, AbilityCatalog>(StringComparer.Ordinal);
        foreach (var catalog in catalogs)
        {
            if (!_catalogs.TryAdd(catalog.GameType, catalog))
            {
                throw new ArgumentException($"Two ability catalogs are registered for {catalog.GameType}.", nameof(catalogs));
            }
        }
    }

    /// <summary>The game's catalog, or null for a game without abilities.</summary>
    public AbilityCatalog? CatalogFor(string gameType) => _catalogs.GetValueOrDefault(gameType);

    /// <summary>
    /// Uses the ability the payload names, <c>{"abilityId": "card_steal", "target": 2}</c>, for the request's player.
    /// <c>target</c> is a seat, given only for abilities that target an opponent.
    /// </summary>
    public AbilityResult Use(AbilityRequest request)
    {
        var match = request.Match;
        var players = match.Members
            .Where(m => m.Seat is not null)
            .OrderBy(m => m.Seat)
            .Select(m => new AbilityPlayer(m.PlayerId, m.Seat!.Value, !m.HasLeft, m.SkipsNextTurn))
            .ToArray();
        if (match.Status != RoomStatus.Active || !players.Any(p => p.PlayerId == request.PlayerId && p.IsPlaying))
        {
            return AbilityResult.Reject(AbilityRefusal.NotPlaying);
        }

        if (!TryReadPayload(request.Payload, out var abilityId, out var targetSeat))
        {
            return AbilityResult.Reject(AbilityRefusal.InvalidPayload);
        }

        var catalog = CatalogFor(match.GameType);
        var member = match.Members.Single(m => m.PlayerId == request.PlayerId);
        if (member.Ability is not { } locked || !string.Equals(locked.Id, abilityId, StringComparison.Ordinal))
        {
            return AbilityResult.Reject(catalog?.Contains(abilityId) == true ? AbilityRefusal.NotYourAbility : AbilityRefusal.UnknownAbility);
        }

        if (member.AbilityUsesLeft == 0)
        {
            return AbilityResult.Reject(AbilityRefusal.UsedUp);
        }

        // The values locked at start apply, even if a deploy changed the catalog since; a handler that is gone or no
        // longer takes those values can't apply them.
        // A value added since the ability was locked takes its handler's default, as the effect was then.
        if (catalog is null || !catalog.TryFind(abilityId, out _, out var handler))
        {
            return AbilityResult.Reject(AbilityRefusal.Unavailable);
        }

        var ability = AbilityCatalog.WithDefaults(locked.ToDefinition(), handler);
        if (!AbilityCatalog.Fits(ability, handler))
        {
            return AbilityResult.Reject(AbilityRefusal.Unavailable);
        }

        AbilityPlayer? target = null;
        if (ability.Target == AbilityTargetKind.Opponent)
        {
            target = players.FirstOrDefault(p => p.Seat == targetSeat && p.IsPlaying && p.PlayerId != request.PlayerId);
            if (target is null)
            {
                return AbilityResult.Reject(AbilityRefusal.InvalidTarget);
            }
        }
        else if (targetSeat is not null)
        {
            return AbilityResult.Reject(AbilityRefusal.InvalidTarget);
        }

        var context = new AbilityContext(match.GameType, request.PlayerId, ability, target, players, request.State, request.Random, match.TurnNumber);
        if (catalog.Timing.CurrentWindow(context) is not { } window || !ability.Windows.Contains(window, StringComparer.Ordinal))
        {
            return AbilityResult.Reject(AbilityRefusal.NotNow);
        }

        if (handler.CanExecute(context) is { } reason)
        {
            if (!AbilityDefinition.IsValidId(reason))
            {
                throw new InvalidOperationException($"The {abilityId} handler refused with a malformed reason.");
            }

            return AbilityResult.Reject(reason);
        }

        var effect = handler.Apply(context);
        if (effect.SkipsNextTurnOf is { } skipped
            && (skipped == request.PlayerId || !players.Any(p => p.PlayerId == skipped && p.IsPlaying)))
        {
            throw new InvalidOperationException($"The {abilityId} handler made a player skip who isn't an opponent still playing.");
        }

        return AbilityResult.Use(ability, target, effect.State, effect.SkipsNextTurnOf);
    }

    /// <summary>An object with a string <c>abilityId</c> and an optional integer <c>target</c>, and nothing else.</summary>
    private static bool TryReadPayload(JsonElement payload, out string abilityId, out int? targetSeat)
    {
        (abilityId, targetSeat) = (string.Empty, null);
        if (payload.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        string? id = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in payload.EnumerateObject())
        {
            if (!seen.Add(property.Name))
            {
                return false;
            }

            switch (property.Name)
            {
                case "abilityId" when property.Value.ValueKind == JsonValueKind.String:
                    id = property.Value.GetString();
                    break;
                case "target" when property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt32(out var seat):
                    targetSeat = seat;
                    break;
                default:
                    return false;
            }
        }

        if (!AbilityDefinition.IsValidId(id))
        {
            return false;
        }

        abilityId = id!;
        return true;
    }
}

/// <param name="Match">The match, read only; the engine reads only its status, game and seats.</param>
/// <param name="PlayerId">The session's player; never taken from the payload.</param>
/// <param name="Payload">The player's choices: which ability and, for some, which seat to target.</param>
/// <param name="State">The game's state, or null before its first accepted command.</param>
/// <param name="Random">The server's source of chance, passed on to the handler.</param>
public sealed record AbilityRequest(GameRoom Match, Guid PlayerId, JsonElement Payload, JsonElement? State, IRandomSource Random);

/// <param name="Used">True when the effect was applied and <see cref="State"/> is the new game state; the caller counts the use.</param>
/// <param name="Reason">When refused: a stable, lower-case code, from <see cref="AbilityRefusal"/> or the game's handler.</param>
/// <param name="Ability">When used: the ability as the player locked it.</param>
/// <param name="Target">When used on an opponent: who.</param>
/// <param name="SkipsNextTurnOf">When used: the opponent the effect makes skip their next turn, if any (STORY-092).</param>
public sealed record AbilityResult(
    bool Used,
    string? Reason = null,
    AbilityDefinition? Ability = null,
    AbilityPlayer? Target = null,
    JsonElement? State = null,
    Guid? SkipsNextTurnOf = null)
{
    public static AbilityResult Use(AbilityDefinition ability, AbilityPlayer? target, JsonElement state, Guid? skipsNextTurnOf = null) =>
        new(true, Ability: ability, Target: target, State: state, SkipsNextTurnOf: skipsNextTurnOf);

    public static AbilityResult Reject(string reason) => new(false, Reason: reason);
}

/// <summary>The engine's stable refusal codes; a game's handler adds its own.</summary>
public static class AbilityRefusal
{
    /// <summary>The match isn't active, or the player isn't seated in it or has left.</summary>
    public const string NotPlaying = "not_playing";

    /// <summary>The payload isn't <c>{"abilityId": "...", "target": seat}</c> with only those fields.</summary>
    public const string InvalidPayload = "invalid_ability_payload";

    /// <summary>The match's game has no ability with that id.</summary>
    public const string UnknownAbility = "unknown_ability";

    /// <summary>The game has the ability, but the player locked another one at start, or none (NFT-016).</summary>
    public const string NotYourAbility = "not_your_ability";

    /// <summary>The player's locked ability has no uses left in this match.</summary>
    public const string UsedUp = "ability_used_up";

    /// <summary>The game can no longer apply the locked ability: its handler is gone or takes other values now.</summary>
    public const string Unavailable = "ability_unavailable";

    /// <summary>A target was missing, not an opponent still playing, or given to an ability that takes none.</summary>
    public const string InvalidTarget = "invalid_target";

    /// <summary>The match isn't in any of the ability's activation windows for the player.</summary>
    public const string NotNow = "ability_not_now";
}
