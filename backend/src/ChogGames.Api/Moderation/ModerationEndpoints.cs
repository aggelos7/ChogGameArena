using System.Security.Claims;
using ChogGames.Api.Profile;
using ChogGames.Infrastructure.Moderation;
using ChogGames.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace ChogGames.Api.Moderation;

/// <summary>
/// The operators' review of reported photos (STORY-069, DEC-070). Operators are the players signed in with a wallet in
/// <c>Moderation:OperatorWallets</c>; to anyone else every route here answers <c>404</c>, as if it didn't exist.
/// </summary>
public static class ModerationEndpoints
{
    /// <summary>A photo with reports to review. Reporters are never named.</summary>
    /// <param name="Reasons">How many reports gave each reason, by reason name in lowercase.</param>
    /// <param name="Hidden">Whether the reports hid it from other players.</param>
    public sealed record ReportedPhotoResponse(
        Guid ImageId,
        string ImageUrl,
        string OwnerName,
        int Reporters,
        IReadOnlyDictionary<string, int> Reasons,
        DateTimeOffset FirstReportedAt,
        DateTimeOffset LastReportedAt,
        bool Hidden);

    public static IEndpointRouteBuilder MapModerationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var moderation = endpoints.MapGroup("/api/moderation").RequireAuthorization().AddEndpointFilter(OperatorsOnlyAsync);

        moderation.MapGet("/reports", ListAsync).WithName("ListPhotoReports");
        moderation.MapDelete("/photos/{imageId:guid}", RemoveAsync).WithName("RemoveReportedPhoto");
        moderation.MapPost("/photos/{imageId:guid}/dismiss", DismissAsync).WithName("DismissPhotoReports");

        return endpoints;
    }

    private static async ValueTask<object?> OperatorsOnlyAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var playerId = Guid.Parse(http.User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var db = http.RequestServices.GetRequiredService<ChogGamesDbContext>();
        var operators = http.RequestServices.GetRequiredService<ModerationOperators>();

        return await db.Players.AsNoTracking().SingleOrDefaultAsync(p => p.Id == playerId, http.RequestAborted) is { } player
            && operators.Includes(player)
                ? await next(context)
                : TypedResults.NotFound();
    }

    private static async Task<Ok<ReportedPhotoResponse[]>> ListAsync(PhotoReportService reports, CancellationToken cancellationToken) =>
        TypedResults.Ok((await reports.ListAsync(cancellationToken))
            .Select(p => new ReportedPhotoResponse(
                p.ImageId,
                AvatarImageEndpoints.UrlFor(p.ImageId),
                p.OwnerName,
                p.Reporters,
                p.Reasons.ToDictionary(r => r.Key.ToString().ToLowerInvariant(), r => r.Value),
                p.FirstReportedAt,
                p.LastReportedAt,
                p.HiddenAt is not null))
            .ToArray());

    /// <summary>Deletes the photo; its owner's built-in avatar shows again.</summary>
    private static async Task<Results<NoContent, NotFound>> RemoveAsync(
        Guid imageId, PhotoReportService reports, CancellationToken cancellationToken) =>
        await reports.RemoveAsync(imageId, cancellationToken) ? TypedResults.NoContent() : TypedResults.NotFound();

    /// <summary>Dismisses the photo's reports and shows it to everyone again.</summary>
    private static async Task<Results<NoContent, NotFound>> DismissAsync(
        Guid imageId, PhotoReportService reports, CancellationToken cancellationToken) =>
        await reports.DismissAsync(imageId, cancellationToken) ? TypedResults.NoContent() : TypedResults.NotFound();
}
