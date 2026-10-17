namespace ChogGames.Infrastructure.Avatars;

/// <summary>Uploaded profile photo settings (<c>AvatarImages</c>, STORY-062, DEC-018).</summary>
public sealed class AvatarImageOptions
{
    public const string SectionName = "AvatarImages";

    /// <summary>Largest upload accepted, in bytes; anything bigger is refused before it is decoded.</summary>
    public int MaxUploadBytes { get; set; } = 2 * 1024 * 1024;

    /// <summary>Largest width or height of an upload, checked from its header before its pixels are decoded.</summary>
    public int MaxSourceDimension { get; set; } = 4096;

    /// <summary>Width and height of the stored square image.</summary>
    public int Size { get; set; } = 256;

    /// <summary>WebP quality of the stored image, 1–100.</summary>
    public int Quality { get; set; } = 80;

    /// <summary>Uploads and removals per player per <see cref="Window"/>; each upload decodes and re-encodes an image.</summary>
    public int ChangesPerWindowPerPlayer { get; set; } = 5;

    public TimeSpan Window { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>How long browsers may keep a served image. Short, so a removed photo soon disappears everywhere.</summary>
    public TimeSpan CacheDuration { get; set; } = TimeSpan.FromHours(1);
}
