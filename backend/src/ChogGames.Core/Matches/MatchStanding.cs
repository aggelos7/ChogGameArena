using System.Text.Json;

namespace ChogGames.Core.Matches;

/// <summary>
/// One player's final place in a finished match (STORY-028, ENG-010, DEC-030), as the game's rules decide it. Ranks
/// count from 1 for the winners; tied players share a rank and the next rank skips the places they took (1, 1, 3).
/// <paramref name="Result"/> is the game's own record of how the player did, such as their final portfolio and
/// statistics (STORY-043, DEC-046): a JSON object stored with their rank, or null for none.
/// </summary>
public sealed record MatchStanding(Guid PlayerId, int Rank, JsonElement? Result = null);
