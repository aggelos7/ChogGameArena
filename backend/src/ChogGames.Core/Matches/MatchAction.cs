namespace ChogGames.Core.Matches;

/// <summary>
/// An accepted command, kept so a retry with the same <see cref="ActionId"/> returns its result instead of applying it
/// again (STORY-024, ENG-009, DEC-026). Rejected commands are not recorded.
/// </summary>
public sealed class MatchAction
{
    public Guid RoomId { get; private set; }

    /// <summary>Client-chosen and unique within the match.</summary>
    public Guid ActionId { get; private set; }

    public Guid PlayerId { get; private set; }

    public string Type { get; private set; } = string.Empty;

    /// <summary>The payload as a JSON object; a retry must send the same one.</summary>
    public string Payload { get; private set; } = "{}";

    /// <summary>The room version this action produced.</summary>
    public long Version { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    private MatchAction()
    {
    }

    internal MatchAction(Guid roomId, Guid actionId, Guid playerId, string type, string payload, long version, DateTimeOffset createdAt)
    {
        RoomId = roomId;
        ActionId = actionId;
        PlayerId = playerId;
        Type = type;
        Payload = payload;
        Version = version;
        CreatedAt = createdAt;
    }
}
