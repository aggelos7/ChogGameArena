namespace ChogGames.Games.ChogRace.Rules;

/// <summary>
/// How far the movement, chaos and attack cards move pawns (RACE-005, STORY-053, DEC-057; STORY-054, DEC-058), part of a
/// rules version and checked
/// by <see cref="RaceRules.Create"/>: every distance is 1 to <see cref="RaceRules.CardMoveLimit"/> tiles, and FOMO's
/// chance of going forward is 1–99%. Airdrop rolls the rules' move again and Whale swaps tiles, so neither has a value.
/// </summary>
/// <param name="Pump">Tiles Pump moves its player forward.</param>
/// <param name="FomoForward">Tiles FOMO moves its player forward when it goes well.</param>
/// <param name="FomoBack">Tiles FOMO moves its player back when it doesn't.</param>
/// <param name="FomoForwardChance">The chance, in percent, that FOMO goes forward.</param>
/// <param name="Reverse">Tiles Reverse moves every opponent back.</param>
/// <param name="Rug">Tiles Rug moves its target back.</param>
public sealed record RaceCardValues(int Pump, int FomoForward, int FomoBack, int FomoForwardChance, int Reverse, int Rug);
