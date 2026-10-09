using ChogGames.Infrastructure.Identity;

namespace ChogGames.Api.Identity;

/// <summary>The only carrier of guest identity: an HttpOnly cookie that scripts cannot read.</summary>
public static class GuestSessionCookie
{
    public const string Name = "chog_session";

    public static string? Read(HttpRequest request) => request.Cookies[Name];

    public static void Write(HttpResponse response, string token, DateTimeOffset expiresAt, GuestSessionOptions options) =>
        response.Cookies.Append(Name, token, new CookieOptions
        {
            HttpOnly = true,
            Secure = options.RequireSecureCookie,
            SameSite = SameSiteMode.Lax,
            Path = "/",
            Expires = expiresAt,
            IsEssential = true,
        });

    public static void Delete(HttpResponse response) =>
        response.Cookies.Delete(Name, new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Lax, Path = "/" });
}
