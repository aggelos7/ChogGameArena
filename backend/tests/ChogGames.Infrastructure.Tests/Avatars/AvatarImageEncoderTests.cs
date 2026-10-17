using System.Text;
using ChogGames.Infrastructure.Avatars;
using SkiaSharp;

namespace ChogGames.Infrastructure.Tests.Avatars;

public sealed class AvatarImageEncoderTests
{
    private const int Size = 256;
    private static readonly AvatarImageEncoder Encoder = new(maxSourceDimension: 1000, Size, quality: 80);

    /// <summary>A <paramref name="width"/>×<paramref name="height"/> image in <paramref name="format"/>, painted by <paramref name="paint"/>.</summary>
    internal static byte[] Image(int width, int height, SKEncodedImageFormat format, Action<SKCanvas>? paint = null)
    {
        using var surface = SKSurface.Create(new SKImageInfo(width, height));
        surface.Canvas.Clear(SKColors.Orange);
        paint?.Invoke(surface.Canvas);
        using var image = surface.Snapshot();
        using var data = image.Encode(format, 95);
        return data.ToArray();
    }

    /// <summary>
    /// <paramref name="jpeg"/> with an EXIF segment right after its start marker: the <paramref name="orientation"/> tag,
    /// then <paramref name="extra"/> bytes inside the segment, as camera metadata such as GPS would be.
    /// </summary>
    internal static byte[] WithExif(byte[] jpeg, ushort orientation, string extra = "")
    {
        byte[] tiff =
        [
            (byte)'I', (byte)'I', 0x2A, 0x00, 0x08, 0x00, 0x00, 0x00, // little-endian TIFF header, IFD at 8
            0x01, 0x00, // one entry
            0x12, 0x01, 0x03, 0x00, 0x01, 0x00, 0x00, 0x00, (byte)orientation, (byte)(orientation >> 8), 0x00, 0x00, // Orientation, SHORT
            0x00, 0x00, 0x00, 0x00, // no next IFD
        ];
        byte[] payload = [.. "Exif\0\0"u8, .. tiff, .. Encoding.ASCII.GetBytes(extra)];
        var length = payload.Length + 2;
        return [jpeg[0], jpeg[1], 0xFF, 0xE1, (byte)(length >> 8), (byte)length, .. payload, .. jpeg[2..]];
    }

    private static SKBitmap Decoded(byte[] webp)
    {
        using var codec = SKCodec.Create(SKData.CreateCopy(webp));
        Assert.Equal(SKEncodedImageFormat.Webp, codec.EncodedFormat);
        return SKBitmap.Decode(codec);
    }

    private static void AssertNear(SKColor expected, SKColor actual)
    {
        const int tolerance = 40; // lossy WebP
        Assert.True(
            Math.Abs(expected.Red - actual.Red) <= tolerance && Math.Abs(expected.Green - actual.Green) <= tolerance
                && Math.Abs(expected.Blue - actual.Blue) <= tolerance,
            $"expected about {expected}, got {actual}");
    }

    [Theory]
    [InlineData(SKEncodedImageFormat.Jpeg)]
    [InlineData(SKEncodedImageFormat.Png)]
    [InlineData(SKEncodedImageFormat.Webp)]
    public void Jpeg_png_and_webp_become_a_small_square_webp(SKEncodedImageFormat format)
    {
        var encoding = Encoder.Encode(Image(640, 480, format));

        Assert.Equal(AvatarImageRefusal.None, encoding.Refusal);
        Assert.Equal("RIFF"u8.ToArray(), encoding.Webp![..4]);
        Assert.Equal("WEBP"u8.ToArray(), encoding.Webp[8..12]);
        using var stored = Decoded(encoding.Webp);
        Assert.Equal((Size, Size), (stored.Width, stored.Height));
        AssertNear(SKColors.Orange, stored.GetPixel(Size / 2, Size / 2));
    }

    [Fact]
    public void A_wide_photo_keeps_its_centre_square()
    {
        // Red and blue bands either side of a green centre square.
        var upload = Image(600, 300, SKEncodedImageFormat.Png, canvas =>
        {
            using var paint = new SKPaint();
            paint.Color = SKColors.Red;
            canvas.DrawRect(0, 0, 150, 300, paint);
            paint.Color = SKColors.Lime;
            canvas.DrawRect(150, 0, 300, 300, paint);
            paint.Color = SKColors.Blue;
            canvas.DrawRect(450, 0, 150, 300, paint);
        });

        using var stored = Decoded(Encoder.Encode(upload).Webp!);

        foreach (var (x, y) in new[] { (2, 2), (Size - 3, 2), (2, Size - 3), (Size - 3, Size - 3), (Size / 2, Size / 2) })
        {
            AssertNear(SKColors.Lime, stored.GetPixel(x, y));
        }
    }

    [Fact]
    public void Metadata_and_trailing_bytes_of_the_upload_are_dropped()
    {
        const string secret = "GPSLatitude=37.9838N GPSLongitude=23.7275E";
        var upload = WithExif(Image(300, 300, SKEncodedImageFormat.Jpeg), orientation: 1, secret);
        upload = [.. upload, .. "<script>alert(1)</script>"u8];

        var webp = Encoder.Encode(upload).Webp!;

        var text = Encoding.Latin1.GetString(webp);
        Assert.DoesNotContain("GPS", text);
        Assert.DoesNotContain("Exif", text);
        Assert.DoesNotContain("EXIF", text);
        Assert.DoesNotContain("XMP", text);
        Assert.DoesNotContain("<script>", text);
    }

    [Fact]
    public void A_rotated_camera_photo_is_stored_upright()
    {
        // Stored with red on top; orientation 6 means the camera was turned, and the photo is shown turned a quarter
        // clockwise, so red ends up on the right.
        var sideways = Image(300, 300, SKEncodedImageFormat.Jpeg, canvas =>
        {
            using var paint = new SKPaint();
            paint.Color = SKColors.Red;
            canvas.DrawRect(0, 0, 300, 150, paint);
            paint.Color = SKColors.Blue;
            canvas.DrawRect(0, 150, 300, 150, paint);
        });

        using var stored = Decoded(Encoder.Encode(WithExif(sideways, orientation: 6)).Webp!);

        // Ignoring the orientation would leave the top left red and the bottom right blue.
        AssertNear(SKColors.Blue, stored.GetPixel(Size / 8, Size / 8));
        AssertNear(SKColors.Red, stored.GetPixel(Size * 7 / 8, Size * 7 / 8));
    }

    public static TheoryData<string, byte[]> NotImages()
    {
        var jpeg = Image(200, 200, SKEncodedImageFormat.Jpeg);
        var png = Image(200, 200, SKEncodedImageFormat.Png);
        return new TheoryData<string, byte[]>
        {
            { "empty", [] },
            { "text", "just some text, saved as photo.jpg"u8.ToArray() },
            { "html", "<!doctype html><script>alert(1)</script>"u8.ToArray() },
            { "svg", """<svg xmlns="http://www.w3.org/2000/svg"><script>alert(1)</script></svg>"""u8.ToArray() },
            { "gif", [.. "GIF89a"u8, 1, 0, 1, 0, 0, 0, 0, .. ","u8, 0, 0, 0, 0, 1, 0, 1, 0, 0, 2, 2, 0x4C, 0x01, 0, .. ";"u8] },
            { "jpeg signature, html body", [0xFF, 0xD8, 0xFF, .. "<html><script>alert(1)</script></html>"u8] },
            { "png signature, garbage", [.. png[..8], .. new byte[400]] },
            { "webp signature, garbage", [.. "RIFF"u8, 0, 1, 0, 0, .. "WEBP"u8, .. new byte[400]] },
            { "truncated jpeg", jpeg[..(jpeg.Length / 2)] },
            { "truncated png", png[..(png.Length / 2)] },
            { "png named jpeg by its first bytes", [0xFF, 0xD8, 0xFF, .. png] },
        };
    }

    [Theory]
    [MemberData(nameof(NotImages))]
    public void Anything_but_a_whole_jpeg_png_or_webp_is_refused(string name, byte[] upload)
    {
        var encoding = Encoder.Encode(upload);

        Assert.True(encoding.Refusal == AvatarImageRefusal.NotAnImage, name);
        Assert.Null(encoding.Webp);
    }

    [Theory]
    [InlineData(1001, 10)]
    [InlineData(10, 1001)]
    public void Images_larger_than_allowed_are_refused_before_decoding(int width, int height)
    {
        var encoding = Encoder.Encode(Image(width, height, SKEncodedImageFormat.Png));

        Assert.Equal(AvatarImageRefusal.TooManyPixels, encoding.Refusal);
        Assert.Null(encoding.Webp);
    }

    [Fact]
    public void Images_at_the_limit_are_accepted()
    {
        Assert.Equal(AvatarImageRefusal.None, Encoder.Encode(Image(1000, 1, SKEncodedImageFormat.Png)).Refusal);
    }
}
