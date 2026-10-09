using System.Security.Claims;
using ChogGames.Core.Players;
using ChogGames.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace ChogGames.Api.Profile;

/// <summary>
/// The caller's own profile (SUITE-004). There is deliberately no player id in any route or
/// body: the profile edited is always the one of the authenticated session's player.
/// </summary>
public static class ProfileEndpoints
{
    public sealed record AvatarResponse(string Id, string Name, string Color);

    public sealed record ProfileResponse(Guid PlayerId, string DisplayName, string Kind, AvatarResponse Avatar, int GamesPlayed, int Wins);

    /// <summary>Only these fields are editable; anything else in the body (ids, counters) is ignored.</summary>
    public sealed record UpdateProfileRequest(string? DisplayName, string? AvatarId);

    public static IEndpointRouteBuilder MapProfileEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var profile = endpoints.MapGroup("/api/profile").RequireAuthorization();

        profile.MapGet("", GetProfileAsync).WithName("GetProfile");
        profile.MapPatch("", UpdateProfileAsync).WithName("UpdateProfile");
        profile.MapGet("/avatars", () => TypedResults.Ok(DefaultAvatar.All.Select(ToResponse).ToArray())).WithName("GetAvatars");

        return endpoints;
    }

    private static async Task<Results<Ok<ProfileResponse>, NotFound>> GetProfileAsync(
        ClaimsPrincipal user, ChogGamesDbContext db, CancellationToken cancellationToken)
    {
        var player = await FindCurrentPlayerAsync(user, db, cancellationToken);
        return player is null ? TypedResults.NotFound() : TypedResults.Ok(ToResponse(player));
    }

    private static async Task<Results<Ok<ProfileResponse>, ValidationProblem, NotFound>> UpdateProfileAsync(
        UpdateProfileRequest? request, ClaimsPrincipal user, ChogGamesDbContext db, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        PlayerDisplayName? displayName = null;
        DefaultAvatar? avatar = null;

        if (request?.DisplayName is null && request?.AvatarId is null)
        {
            errors[""] = ["Provide displayName and/or avatarId."];
        }

        if (request?.DisplayName is not null && !PlayerDisplayName.TryCreate(request.DisplayName, out displayName))
        {
            errors["displayName"] = [PlayerDisplayName.Rule];
        }

        if (request?.AvatarId is not null && !DefaultAvatar.TryFind(request.AvatarId, out avatar))
        {
            errors["avatarId"] = [$"Unknown avatar. Choose one of: {string.Join(", ", DefaultAvatar.All.Select(a => a.Id))}."];
        }

        // Validate everything before touching the player, so a partly invalid request changes nothing.
        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        var player = await FindCurrentPlayerAsync(user, db, cancellationToken);
        if (player is null)
        {
            return TypedResults.NotFound();
        }

        if (displayName is not null)
        {
            player.Rename(displayName);
        }

        if (avatar is not null)
        {
            player.ChangeAvatar(avatar);
        }

        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(ToResponse(player));
    }

    private static Task<Player?> FindCurrentPlayerAsync(ClaimsPrincipal user, ChogGamesDbContext db, CancellationToken cancellationToken)
    {
        var playerId = Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);
        return db.Players.SingleOrDefaultAsync(p => p.Id == playerId, cancellationToken);
    }

    private static ProfileResponse ToResponse(Player player)
    {
        // An avatar retired from the built-in list falls back to the default rather than failing.
        var avatar = DefaultAvatar.TryFind(player.AvatarId, out var found) ? found : DefaultAvatar.Classic;
        return new ProfileResponse(player.Id, player.DisplayName, player.Kind.ToString(), ToResponse(avatar), player.GamesPlayed, player.Wins);
    }

    private static AvatarResponse ToResponse(DefaultAvatar avatar) => new(avatar.Id, avatar.Name, avatar.Color);
}
