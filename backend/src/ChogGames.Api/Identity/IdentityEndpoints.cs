using System.Security.Claims;
using System.Threading.RateLimiting;
using ChogGames.Core.Players;
using ChogGames.Infrastructure.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;

namespace ChogGames.Api.Identity;

public static class IdentityEndpoints
{
    public const string GuestCreationRateLimitPolicy = "guest-session-create";

    public sealed record CreateGuestSessionRequest(string? DisplayName);

    public sealed record CurrentPlayerResponse(Guid PlayerId, string DisplayName, string Kind);

    public static IServiceCollection AddGuestIdentity(this IServiceCollection services)
    {
        services.AddAuthentication(GuestSessionAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, GuestSessionAuthenticationHandler>(GuestSessionAuthenticationHandler.SchemeName, null);
        services.AddAuthorization();

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy(GuestCreationRateLimitPolicy, context =>
            {
                var permitLimit = context.RequestServices.GetRequiredService<IOptions<GuestSessionOptions>>().Value.CreatePerMinutePerClient;
                return RateLimitPartition.GetFixedWindowLimiter(
                    context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions { PermitLimit = permitLimit, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 });
            });
        });

        return services;
    }

    public static IEndpointRouteBuilder MapIdentityEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/guest-sessions", CreateGuestSessionAsync)
            .RequireRateLimiting(GuestCreationRateLimitPolicy)
            .WithName("CreateGuestSession");

        endpoints.MapGet("/api/me", GetCurrentPlayer)
            .RequireAuthorization()
            .WithName("GetCurrentPlayer");

        return endpoints;
    }

    /// <summary>
    /// Creates a guest identity, or returns the caller's existing one when the request already
    /// carries a valid session cookie, so retries and refreshes never create duplicate guests.
    /// </summary>
    private static async Task<Results<Ok<CurrentPlayerResponse>, Created<CurrentPlayerResponse>, ValidationProblem>> CreateGuestSessionAsync(
        CreateGuestSessionRequest? request,
        HttpContext context,
        GuestSessionService sessions,
        IOptions<GuestSessionOptions> options,
        CancellationToken cancellationToken)
    {
        var existing = await context.AuthenticateAsync(GuestSessionAuthenticationHandler.SchemeName);
        if (existing.Succeeded)
        {
            return TypedResults.Ok(ToResponse(existing.Principal));
        }

        PlayerDisplayName? displayName = null;
        if (request?.DisplayName is not null && !PlayerDisplayName.TryCreate(request.DisplayName, out displayName))
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["displayName"] = [PlayerDisplayName.Rule],
            });
        }

        var issued = await sessions.CreateAsync(displayName, cancellationToken);
        GuestSessionCookie.Write(context.Response, issued.Token, issued.ExpiresAt, options.Value);

        return TypedResults.Created("/api/me", new CurrentPlayerResponse(issued.Player.Id, issued.Player.DisplayName, issued.Player.Kind.ToString()));
    }

    private static Ok<CurrentPlayerResponse> GetCurrentPlayer(ClaimsPrincipal user) => TypedResults.Ok(ToResponse(user));

    private static CurrentPlayerResponse ToResponse(ClaimsPrincipal user) => new(
        Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!),
        user.FindFirstValue(ClaimTypes.Name)!,
        user.FindFirstValue(GuestSessionAuthenticationHandler.PlayerKindClaim)!);
}
