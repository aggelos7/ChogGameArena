using System.Security.Claims;
using System.Text.Encodings.Web;
using ChogGames.Infrastructure.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace ChogGames.Api.Identity;

/// <summary>
/// Authenticates requests solely from the <see cref="GuestSessionCookie"/>. Player ids in
/// routes, query strings, headers or bodies are never used to establish identity.
/// </summary>
public sealed class GuestSessionAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> schemeOptions,
    ILoggerFactory logger,
    UrlEncoder encoder,
    GuestSessionService sessions,
    IOptions<GuestSessionOptions> sessionOptions)
    : AuthenticationHandler<AuthenticationSchemeOptions>(schemeOptions, logger, encoder)
{
    public const string SchemeName = "GuestSession";
    public const string PlayerKindClaim = "player_kind";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var token = GuestSessionCookie.Read(Request);
        if (string.IsNullOrEmpty(token))
        {
            return AuthenticateResult.NoResult();
        }

        var session = await sessions.AuthenticateAsync(token, Context.RequestAborted);
        if (session is null)
        {
            return AuthenticateResult.Fail("Invalid or expired guest session.");
        }

        if (session.Renewed)
        {
            GuestSessionCookie.Write(Response, token, session.ExpiresAt, sessionOptions.Value);
        }

        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, session.Player.Id.ToString()),
                new Claim(ClaimTypes.Name, session.Player.DisplayName),
                new Claim(PlayerKindClaim, session.Player.Kind.ToString()),
            ],
            SchemeName);

        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }
}
