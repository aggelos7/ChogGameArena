using System.Net.Http.Headers;
using System.Security.Claims;
using System.Threading.RateLimiting;
using ChogGames.Core.Players;
using ChogGames.Infrastructure.Avatars;
using ChogGames.Infrastructure.Moderation;
using ChogGames.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
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

    public const string ReportRateLimitPolicy = "photo-reports";

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
        public const string TermsNotAccepted = "terms_not_accepted";
        public const string InvalidReportReason = "invalid_report_reason";
        public const string OwnPhoto = "report_own_photo";
        public const string AlreadyReported = "photo_already_reported";
    }

    /// <param name="Reason">One of <see cref="AvatarImageReportReason"/>'s names, in any case.</param>
    public sealed record ReportPhotoRequest(string? Reason);

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
        services.AddRateLimiter(options => options.AddPolicy(ReportRateLimitPolicy, context =>
        {
            // Per player, refused reports included, so reports can't be used to probe or flood.
            var moderation = context.RequestServices.GetRequiredService<IOptions<ModerationOptions>>().Value;
            return RateLimitPartition.GetFixedWindowLimiter(
                context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "anonymous",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = moderation.ReportsPerWindowPerPlayer, Window = moderation.Window, QueueLimit = 0,
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
        endpoints.MapPost($"{ImagePath}/{{imageId:guid}}/reports", ReportAsync)
            .RequireAuthorization()
            .RequireRateLimiting(ReportRateLimitPolicy)
            .WithName("ReportAvatarImage");

        return endpoints;
    }

    private static async Task<Results<Ok<ProfileEndpoints.ProfileResponse>, ProblemHttpResult, NotFound>> UploadAsync(
        HttpRequest request,
        ClaimsPrincipal user,
        AvatarImageEncoder encoder,
        AvatarImageService images,
        ChogGamesDbContext db,
        ModerationOperators operators,
        IOptions<AvatarImageOptions> options,
        CancellationToken cancellationToken)
    {
        var maxBytes = options.Value.MaxUploadBytes;
        var playerId = PlayerIdOf(user);

        // Checked first, so nothing is read or decoded for a player who may not upload yet (DEC-070).
        if (await db.Players.AsNoTracking().Where(p => p.Id == playerId).Select(p => p.TermsVersion).ToListAsync(cancellationToken)
            is not [var termsVersion])
        {
            return TypedResults.NotFound();
        }

        if (termsVersion != TermsOfUse.CurrentVersion)
        {
            return Problem(StatusCodes.Status403Forbidden, ErrorCodes.TermsNotAccepted,
                "Accept the terms of use before uploading a photo.");
        }

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

        if (!await images.ReplaceAsync(playerId, encoding.Webp!, cancellationToken))
        {
            return TypedResults.NotFound();
        }

        return await ProfileEndpoints.FindProfileAsync(playerId, db, operators, cancellationToken) is { } profile
            ? TypedResults.Ok(profile)
            : TypedResults.NotFound();
    }

    /// <summary>Deletes the caller's photo, if any, and shows their built-in avatar again; idempotent.</summary>
    private static async Task<Results<Ok<ProfileEndpoints.ProfileResponse>, NotFound>> RemoveAsync(
        ClaimsPrincipal user, AvatarImageService images, ChogGamesDbContext db, ModerationOperators operators, CancellationToken cancellationToken)
    {
        var playerId = PlayerIdOf(user);
        if (!await images.RemoveAsync(playerId, cancellationToken))
        {
            return TypedResults.NotFound();
        }

        return await ProfileEndpoints.FindProfileAsync(playerId, db, operators, cancellationToken) is { } profile
            ? TypedResults.Ok(profile)
            : TypedResults.NotFound();
    }

    /// <summary>
    /// Any signed-in player may see any player's photo, as they see it wherever the avatar is shown. Only the stored
    /// WebP is ever served, with a fixed type that browsers must not sniff, and only briefly cached, so a removed photo
    /// soon disappears. A photo that reports hid is served only to its owner and to operators (DEC-070).
    /// </summary>
    private static async Task<Results<FileContentHttpResult, NotFound>> GetImageAsync(
        Guid imageId,
        ClaimsPrincipal user,
        HttpResponse response,
        AvatarImageService images,
        ChogGamesDbContext db,
        ModerationOperators operators,
        IOptions<AvatarImageOptions> options,
        CancellationToken cancellationToken)
    {
        if (await images.FindAsync(imageId, cancellationToken) is not { } image)
        {
            return TypedResults.NotFound();
        }

        var viewerId = PlayerIdOf(user);
        if (image.Hidden && image.PlayerId != viewerId && !await IsOperatorAsync(viewerId, db, operators, cancellationToken))
        {
            return TypedResults.NotFound();
        }

        response.Headers.XContentTypeOptions = "nosniff";
        response.Headers.CacheControl = $"private, max-age={(int)options.Value.CacheDuration.TotalSeconds}";
        response.Headers.ContentSecurityPolicy = "default-src 'none'; sandbox";
        return TypedResults.File(image.Webp, AvatarImage.ContentType);
    }

    /// <summary>
    /// Reports another player's photo with a fixed reason (STORY-069, DEC-070), once per photo. A photo that is gone, or
    /// was replaced, is <c>404</c>.
    /// </summary>
    private static async Task<Results<NoContent, ProblemHttpResult, NotFound>> ReportAsync(
        Guid imageId, ReportPhotoRequest? request, ClaimsPrincipal user, PhotoReportService reports, CancellationToken cancellationToken)
    {
        // Names only: Enum.TryParse would also take numbers.
        if (Enum.GetValues<AvatarImageReportReason>()
                .Where(r => string.Equals(r.ToString(), request?.Reason, StringComparison.OrdinalIgnoreCase))
                .Select(r => (AvatarImageReportReason?)r)
                .SingleOrDefault() is not { } reason)
        {
            return Problem(StatusCodes.Status400BadRequest, ErrorCodes.InvalidReportReason,
                $"Choose a reason: {string.Join(", ", Enum.GetNames<AvatarImageReportReason>().Select(n => n.ToLowerInvariant()))}.");
        }

        return await reports.ReportAsync(PlayerIdOf(user), imageId, reason, cancellationToken) switch
        {
            PhotoReportOutcome.Reported => TypedResults.NoContent(),
            PhotoReportOutcome.OwnPhoto => Problem(StatusCodes.Status400BadRequest, ErrorCodes.OwnPhoto, "You can't report your own photo."),
            PhotoReportOutcome.AlreadyReported => Problem(StatusCodes.Status409Conflict, ErrorCodes.AlreadyReported,
                "You have already reported this photo."),
            _ => TypedResults.NotFound(),
        };
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

    private static async Task<bool> IsOperatorAsync(
        Guid playerId, ChogGamesDbContext db, ModerationOperators operators, CancellationToken cancellationToken) =>
        await db.Players.AsNoTracking().SingleOrDefaultAsync(p => p.Id == playerId, cancellationToken) is { } player
        && operators.Includes(player);

    private static Guid PlayerIdOf(ClaimsPrincipal user) => Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);

    private static ProblemHttpResult Problem(int status, string code, string detail) =>
        TypedResults.Problem(detail, statusCode: status, extensions: new Dictionary<string, object?> { ["code"] = code });
}
