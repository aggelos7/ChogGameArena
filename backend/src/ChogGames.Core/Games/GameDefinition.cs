namespace ChogGames.Core.Games;

/// <summary>
/// A game the platform can host rooms for, identified by its stable game type (STORY-019, ENG-001). Definitions
/// are added by reviewed migrations; whether a game is currently enabled comes from the game catalogue.
/// </summary>
public sealed class GameDefinition
{
    public const int MaxIdLength = 32;

    /// <summary>Fewest players any room may start with (ENG-001).</summary>
    public const int MinRoomPlayers = 2;

    /// <summary>Most players any room may hold (ENG-001).</summary>
    public const int MaxRoomPlayers = 6;

    public const string ChogLife = "chog-life";

    public const string ChogRace = "chog-race";

    public string Id { get; private set; } = string.Empty;

    public int MinPlayers { get; private set; }

    public int MaxPlayers { get; private set; }

    private GameDefinition()
    {
    }

    public static GameDefinition Create(string id, int minPlayers, int maxPlayers)
    {
        if (string.IsNullOrEmpty(id) || id.Length > MaxIdLength)
        {
            throw new ArgumentException($"A game id must have 1 to {MaxIdLength} characters.", nameof(id));
        }

        if (minPlayers < MinRoomPlayers || maxPlayers > MaxRoomPlayers || minPlayers > maxPlayers)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxPlayers), $"Player limits must satisfy {MinRoomPlayers} <= min <= max <= {MaxRoomPlayers}.");
        }

        return new GameDefinition { Id = id, MinPlayers = minPlayers, MaxPlayers = maxPlayers };
    }
}
