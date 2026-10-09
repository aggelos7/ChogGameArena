namespace ChogGames.Core.Games;

/// <summary>Catalogue availability of a game (SUITE-001). Only <see cref="Available"/> games can be launched.</summary>
public enum GameStatus
{
    Available,
    ComingSoon,
    Maintenance,
}
