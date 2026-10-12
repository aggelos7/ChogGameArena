namespace ChogGames.Core.Matches;

/// <summary>
/// One player's final place in a finished match (STORY-028, ENG-010, DEC-030), as the game's rules decide it. Ranks
/// count from 1 for the winners; tied players share a rank and the next rank skips the places they took (1, 1, 3).
/// </summary>
public sealed record MatchStanding(Guid PlayerId, int Rank);
