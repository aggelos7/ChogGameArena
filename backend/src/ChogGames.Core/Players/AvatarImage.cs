namespace ChogGames.Core.Players;

/// <summary>
/// A player's uploaded profile photo (STORY-062, DEC-018), as the server re-encoded it: a small square WebP without
/// metadata. The bytes the player sent are never stored. One per player; a new upload replaces it with a new id, so the
/// id in an image URL never names the player and a cached URL never shows a newer photo.
/// </summary>
public sealed class AvatarImage
{
    /// <summary>The WebP media type every stored image has.</summary>
    public const string ContentType = "image/webp";

    public Guid Id { get; private set; }

    public Guid PlayerId { get; private set; }

    public byte[] Data { get; private set; } = [];

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>
    /// When enough players reported the photo for it to be hidden from everyone but its owner and operators (STORY-069,
    /// DEC-070); null while it shows. Set and cleared only by the server's report handling.
    /// </summary>
    public DateTimeOffset? HiddenAt { get; private set; }

    private AvatarImage()
    {
    }

    /// <param name="webp">An image the server has just re-encoded; never bytes as uploaded.</param>
    public static AvatarImage Create(Guid playerId, byte[] webp, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(webp);
        if (webp.Length == 0)
        {
            throw new ArgumentException("An avatar image needs its encoded bytes.", nameof(webp));
        }

        return new AvatarImage { Id = Guid.NewGuid(), PlayerId = playerId, Data = webp, CreatedAt = now };
    }
}
