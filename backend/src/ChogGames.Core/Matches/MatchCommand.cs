using System.Text.Json;
using System.Text.RegularExpressions;

namespace ChogGames.Core.Matches;

/// <summary>
/// A player's intent for a match (STORY-024, ENG-003, ENG-009, DEC-026). The player is never part of it; it comes from
/// the session. <paramref name="ActionId"/> makes retries idempotent within the match, and
/// <paramref name="ExpectedVersion"/> is the room version the client acted on, so commands built on old state are refused.
/// </summary>
/// <param name="MatchId">The room's id; the room is the match (DEC-025).</param>
/// <param name="Type">An upper-case intent name such as <c>ROLL</c>; the game's rules decide what it means.</param>
/// <param name="Payload">A JSON object with the intent's details; an empty object when there are none.</param>
public sealed partial record MatchCommand(Guid ActionId, Guid MatchId, long ExpectedVersion, string Type, JsonElement Payload)
{
    public const int MaxTypeLength = 32;

    /// <summary>
    /// Uses the player's locked ability, <c>{"abilityId": "...", "target": seat}</c>. The shared engine handles it for
    /// every game, never the game's rules (STORY-034, ENG-003, DEC-036).
    /// </summary>
    public const string UseAbility = "USE_ABILITY";

    /// <summary>Largest payload accepted, in characters of its JSON text.</summary>
    public const int MaxPayloadLength = 4096;

    /// <summary>Upper-case letters, digits and underscores, starting with a letter.</summary>
    public static bool IsValidType(string? type) => type is not null && TypeFormat().IsMatch(type);

    [GeneratedRegex("^[A-Z][A-Z0-9_]{0,31}$")]
    private static partial Regex TypeFormat();
}
