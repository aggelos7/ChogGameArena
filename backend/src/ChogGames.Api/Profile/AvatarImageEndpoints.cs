using System.Net.Http.Headers;
using System.Security.Claims;
using System.Threading.RateLimiting;
using ChogGames.Core.Players;
using ChogGames.Infrastructure.Avatars;
using ChogGames.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;

namespace ChogGames.Api.Profile;

/// <summary>
/// The caller's uploaded profile photo (STORY-062, DEC-018). The upload is the request body itself, typed
/// <c>image/jpeg</c>, <c>image/png</c> or <c>image/webp</c>; the server re-encodes it to a square WebP and stores only
/// that. Images are served by an id of their own, never by player id, and only ever as the stored WebP.
/// </summary>
public static class AvatarImageEndpoints
{
    public const string RateLimitPolicy = "avatar-images";

    public const string ImagePath = "/api/avatar-images";

    /// <summary>Declared types accepted; the content decides the actual format.</summary>
    public static readonly string[] AcceptedTypes = ["image/jpeg", "image/png", "image/webp"];

    /// <summary>Stable error codes; clients rely on these, never on the message text.</summary>
    public static class ErrorCodes
    {
        public const string TooLarge = "avatar_too_large";
        public const string UnsupportedType = "avatar_unsupported_type";
        public const string NotAnImage = "avatar_not_an_image";
        public const string TooManyPixels = "avatar_too_many_pixels";
    }

    /// <summary>Where the image with this id is served.</summary>
    public static string UrlFor(Guid imageId) => $"{ImagePath}/{imageId}";

    public static IServiceCollection AddAvatarImages(this IServiceCollection services)
    {
        services.AddRateLimiter(options => options.AddPolicy(RateLimitPolicy, context =>
        {
            // Per player: every upload decodes and re-encodes an image.
            var avatarImages = context.RequestServices.GetRequiredService<IOptions<AvatarImageOptions>>().Value;
            return RateLimitPartition.GetFixedWindowLimiter(
                context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "anonymous",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = avatarImages.ChangesPerWindowPerPlayer, Window = avatarImages.Window, QueueLimit = 0,
                });
        }));

        return services;
    }

    public static IEndpointRouteBuilder MapAvatarImageEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var own = endpoints.MapGroup("/api/profile/avatar-image").RequireAuthorization().RequireRateLimiting(RateLimitPolicy);
        own.MapPut("", UploadAsync).WithName("UploadAvatarImage");
        own.MapDelete("", RemoveAsync).WithName("RemoveAvatarImage");

        endpoints.MapGet($"{ImagePath}/{{imageId:guid}}", GetImageAsync).RequireAuthorization().WithName("GetAvatarImage");

        return endpoints;
    }

    private static async Task<Results<Ok<ProfileEndpoints.ProfileResponse>, ProblemHttpResult, NotFound>> UploadAsync(
        HttpRequest request,
        ClaimsPrincipal user,
        AvatarImageEncoder encoder,
        AvatarImageService images,
        ChogGamesDbContext db,
        IOptions<AvatarImageOptions> options,
        CancellationToken cancellationToken)
    {
        var maxBytes = options.Value.MaxUploadBytes;

        if (!MediaTypeHeaderValue.TryParse(request.ContentType, out var declared)
            || !AcceptedTypes.Contains(declared.MediaType, StringComparer.OrdinalIgnoreCase))
        {
            return Problem(StatusCodes.Status415UnsupportedMediaType, ErrorCodes.UnsupportedType,
                $"Upload a photo as one of: {string.Join(", ", AcceptedTypes)}.");
        }

        if (request.ContentLength > maxBytes || await ReadUpToAsync(request.Body, maxBytes, cancellationToken) is not { } upload)
        {
            return Problem(StatusCodes.Status413PayloadTooLarge, ErrorCodes.TooLarge, $"The photo must be at most {maxBytes} bytes.");
        }

        var encoding = encoder.Encode(upload);
        switch (encoding.Refusal)
        {
            case AvatarImageRefusal.NotAnImage:
                return Problem(StatusCodes.Status400BadRequest, ErrorCodes.NotAnImage, "The file is not a readable JPEG, PNG or WebP image.");
            case AvatarImageRefusal.TooManyPixels:
                return Problem(StatusCodes.Status400BadRequest, ErrorCodes.TooManyPixels,
                    $"The photo must be at most {options.Value.MaxSourceDimension} pixels wide and high.");
        }

        var playerId = PlayerIdOf(user);
        if (!await images.ReplaceAsync(playerId, encoding.Webp!, cancellationToken))
        {
            return TypedResults.NotFound();
        }

        return await ProfileEndpoints.FindProfileAsync(playerId, db, cancellationToken) is { } profile
            ? TypedResults.Ok(profile)
            : TypedResults.NotFound();
    }

    /// <summary>Deletes the caller's photo, if any, and shows their built-in avatar again; idempotent.</summary>
    private static async Task<Results<Ok<ProfileEndpoints.ProfileResponse>, NotFound>> RemoveAsync(
        ClaimsPrincipal user, AvatarImageService images, ChogGamesDbContext db, CancellationToken cancellationToken)
    {
        var playerId = PlayerIdOf(user);
        if (!await images.RemoveAsync(playerId, cancellationToken))
        {
            return TypedResults.NotFound();
        }

        return await ProfileEndpoints.FindProfileAsync(playerId, db, cancellationToken) is { } profile
            ? TypedResults.Ok(profile)
            : TypedResults.NotFound();
    }

    /// <summary>
    /// Any signed-in player may see any player's photo, as they see it wherever the avatar is shown. Only the stored
    /// WebP is ever served, with a fixed type that browsers must not sniff, and only briefly cached, so a removed photo
    /// soon disappears.
    /// </summary>
    private static async Task<Results<FileContentHttpResult, NotFound>> GetImageAsync(
        Guid imageId, HttpResponse response, AvatarImageService images, IOptions<AvatarImageOptions> options, CancellationToken cancellationToken)
    {
        if (await images.FindAsync(imageId, cancellationToken) is not { } webp)
        {
            return TypedResults.NotFound();
        }

        response.Headers.XContentTypeOptions = "nosniff";
        response.Headers.CacheControl = $"private, max-age={(int)options.Value.CacheDuration.TotalSeconds}";
        response.Headers.ContentSecurityPolicy = "default-src 'none'; sandbox";
        return TypedResults.File(webp, AvatarImage.ContentType);
    }

    /// <summary>The body, or null when it is longer than <paramref name="maxBytes"/>, which also covers a missing Content-Length.</summary>
    private static async Task<byte[]?> ReadUpToAsync(Stream body, int maxBytes, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await body.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > maxBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static Guid PlayerIdOf(ClaimsPrincipal user) => Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);

    private static ProblemHttpResult Problem(int status, string code, string detail) =>
        TypedResults.Problem(detail, statusCode: status, extensions: new Dictionary<string, object?> { ["code"] = code });
}
