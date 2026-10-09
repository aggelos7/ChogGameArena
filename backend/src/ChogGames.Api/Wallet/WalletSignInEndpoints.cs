using System.Security.Claims;
using System.Threading.RateLimiting;
using ChogGames.Api.Identity;
using ChogGames.Core.Wallets;
using ChogGames.Infrastructure.Identity;
using ChogGames.Infrastructure.Wallets;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;

namespace ChogGames.Api.Wallet;

/// <summary>Challenge-sign wallet authentication (STORY-013, SUITE-003). All endpoints act only on the caller's own session.</summary>
public static class WalletSignInEndpoints
{
    public const string RateLimitPolicy = "wallet-sign-in";

    public sealed record ChallengeRequest(string? Address);

    public sealed record ChallengeResponse(Guid ChallengeId, string Message, DateTimeOffset ExpiresAt);

    public sealed record VerifyRequest(Guid? ChallengeId, string? Signature);

    public static IServiceCollection AddWalletSignIn(this IServiceCollection services)
    {
        services.AddRateLimiter(options => options.AddPolicy(RateLimitPolicy, context =>
        {
            var permitLimit = context.RequestServices.GetRequiredService<IOptions<WalletAuthOptions>>().Value.RequestsPerMinutePerClient;
            return RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = permitLimit, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 });
        }));

        return services;
    }

    public static IEndpointRouteBuilder MapWalletSignInEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/wallet").RequireAuthorization();

        group.MapPost("/challenge", CreateChallengeAsync).RequireRateLimiting(RateLimitPolicy).WithName("CreateWalletChallenge");
        group.MapPost("/verify", VerifyAsync).RequireRateLimiting(RateLimitPolicy).WithName("VerifyWalletSignature");
        group.MapPost("/sign-out", SignOutAsync).WithName("SignOut");

        return endpoints;
    }

    /// <summary>Issues a one-time EIP-4361 message for the caller to sign. Grants nothing by itself.</summary>
    private static async Task<Results<Ok<ChallengeResponse>, ValidationProblem>> CreateChallengeAsync(
        ChallengeRequest? request,
        ClaimsPrincipal user,
        WalletSignInService signIn,
        IOptions<WalletChainOptions> chain,
        CancellationToken cancellationToken)
    {
        if (!WalletAddress.TryParse(request?.Address, out var address))
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["address"] = ["Address must be 0x followed by 40 hexadecimal characters."],
            });
        }

        var challenge = await signIn.CreateChallengeAsync(PlayerId(user), address, chain.Value.ChainId, cancellationToken);
        return TypedResults.Ok(new ChallengeResponse(challenge.Id, challenge.Message, challenge.ExpiresAt));
    }

    /// <summary>Verifies the signature; on success binds the wallet and replaces the session cookie.</summary>
    private static async Task<Results<Ok<IdentityEndpoints.CurrentPlayerResponse>, ValidationProblem>> VerifyAsync(
        VerifyRequest? request,
        HttpContext context,
        WalletSignInService signIn,
        IOptions<GuestSessionOptions> sessionOptions,
        CancellationToken cancellationToken)
    {
        if (request?.ChallengeId is not { } challengeId)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["challengeId"] = ["Challenge id is required."] });
        }

        var result = await signIn.VerifyAsync(
            PlayerId(context.User),
            GuestSessionCookie.Read(context.Request)!,
            challengeId,
            request.Signature,
            cancellationToken);

        switch (result.Failure)
        {
            case WalletSignInFailure.ChallengeUnavailable:
                return TypedResults.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["challengeId"] = ["This sign-in request expired or was already used. Start again."],
                });
            case WalletSignInFailure.InvalidSignature:
                return TypedResults.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["signature"] = ["The signature does not match this wallet and sign-in request. Start again."],
                });
        }

        var issued = result.Session!;
        GuestSessionCookie.Write(context.Response, issued.Token, issued.ExpiresAt, sessionOptions.Value);
        return TypedResults.Ok(new IdentityEndpoints.CurrentPlayerResponse(
            issued.Player.Id,
            issued.Player.DisplayName,
            issued.Player.Kind.ToString(),
            issued.Player.WalletAddress));
    }

    /// <summary>Ends the caller's session (wallet sign-out). The browser then starts a fresh guest.</summary>
    private static async Task<NoContent> SignOutAsync(HttpContext context, GuestSessionService sessions, CancellationToken cancellationToken)
    {
        await sessions.SignOutAsync(GuestSessionCookie.Read(context.Request), cancellationToken);
        GuestSessionCookie.Delete(context.Response);
        return TypedResults.NoContent();
    }

    private static Guid PlayerId(ClaimsPrincipal user) => Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);
}
