using SkiaSharp;

namespace ChogGames.Infrastructure.Avatars;

public enum AvatarImageRefusal
{
    None,

    /// <summary>Not a JPEG, PNG or WebP by its content, whatever its name or declared type, or not decodable in full.</summary>
    NotAnImage,

    /// <summary>A valid image wider or taller than allowed.</summary>
    TooManyPixels,
}

/// <param name="Webp">The re-encoded image; null when refused.</param>
public sealed record AvatarImageEncoding(byte[]? Webp, AvatarImageRefusal Refusal);

/// <summary>
/// Turns an uploaded photo into a stored avatar (STORY-062, DEC-018): the format is read from the content, never from a
/// name or a declared type; the image is decoded in full, turned upright from its EXIF orientation, cropped to its
/// centre square, scaled down and encoded again as WebP. Only pixels survive, so EXIF, GPS and any other metadata or
/// trailing bytes of the upload are dropped.
/// </summary>
public sealed class AvatarImageEncoder(int maxSourceDimension, int size, int quality)
{
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public AvatarImageEncoding Encode(byte[] upload)
    {
        if (Sniff(upload) is not { } format)
        {
            return Refused(AvatarImageRefusal.NotAnImage);
        }

        using var data = SKData.CreateCopy(upload);
        using var codec = SKCodec.Create(data);
        // The decoder must agree with the signature, so a file can't pass as one format and be decoded as another.
        if (codec is null || codec.EncodedFormat != format || codec.Info.Width < 1 || codec.Info.Height < 1)
        {
            return Refused(AvatarImageRefusal.NotAnImage);
        }

        if (codec.Info.Width > maxSourceDimension || codec.Info.Height > maxSourceDimension)
        {
            return Refused(AvatarImageRefusal.TooManyPixels);
        }

        using var source = new SKBitmap(new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
        // Anything but a full decode, such as a truncated file, is refused rather than stored half grey.
        if (codec.GetPixels(source.Info, source.GetPixels()) != SKCodecResult.Success)
        {
            return Refused(AvatarImageRefusal.NotAnImage);
        }

        var side = Math.Min(source.Width, source.Height);
        var crop = SKRect.Create((source.Width - side) / 2, (source.Height - side) / 2, side, side);

        using var surface = SKSurface.Create(new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        canvas.SetMatrix(Upright(codec.EncodedOrigin, size));
        using (var sourceImage = SKImage.FromBitmap(source))
        {
            canvas.DrawImage(sourceImage, crop, SKRect.Create(size, size), new SKSamplingOptions(SKCubicResampler.Mitchell));
        }

        canvas.Flush();
        using var result = surface.Snapshot();
        using var webp = result.Encode(SKEncodedImageFormat.Webp, quality);
        return webp is null ? Refused(AvatarImageRefusal.NotAnImage) : new AvatarImageEncoding(webp.ToArray(), AvatarImageRefusal.None);
    }

    /// <summary>The format the content's signature names, if it is one of the accepted three.</summary>
    private static SKEncodedImageFormat? Sniff(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
        {
            return SKEncodedImageFormat.Jpeg;
        }

        if (bytes.AsSpan().StartsWith(PngSignature))
        {
            return SKEncodedImageFormat.Png;
        }

        if (bytes.Length >= 12 && bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) && bytes.AsSpan(8, 4).SequenceEqual("WEBP"u8))
        {
            return SKEncodedImageFormat.Webp;
        }

        return null;
    }

    /// <summary>
    /// Draws a square stored as <paramref name="origin"/> upright, as the camera meant it (Skia's own origin matrices,
    /// with width and height both <paramref name="side"/>).
    /// </summary>
    private static SKMatrix Upright(SKEncodedOrigin origin, float side) => origin switch
    {
        SKEncodedOrigin.TopRight => new SKMatrix(-1, 0, side, 0, 1, 0, 0, 0, 1),
        SKEncodedOrigin.BottomRight => new SKMatrix(-1, 0, side, 0, -1, side, 0, 0, 1),
        SKEncodedOrigin.BottomLeft => new SKMatrix(1, 0, 0, 0, -1, side, 0, 0, 1),
        SKEncodedOrigin.LeftTop => new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1),
        SKEncodedOrigin.RightTop => new SKMatrix(0, -1, side, 1, 0, 0, 0, 0, 1),
        SKEncodedOrigin.RightBottom => new SKMatrix(0, -1, side, -1, 0, side, 0, 0, 1),
        SKEncodedOrigin.LeftBottom => new SKMatrix(0, 1, 0, -1, 0, side, 0, 0, 1),
        _ => SKMatrix.Identity,
    };

    private static AvatarImageEncoding Refused(AvatarImageRefusal refusal) => new(null, refusal);
}
