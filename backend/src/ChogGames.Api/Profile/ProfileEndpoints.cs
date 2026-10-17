using System.Security.Claims;
using ChogGames.Core.Achievements;
using ChogGames.Core.Players;
using ChogGames.Infrastructure.Achievements;
using ChogGames.Infrastructure.Persistence;
using ChogGames.Infrastructure.Progression;
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

    /// <param name="Xp">All the XP the player has earned (STORY-059, DEC-064).</param>
    public sealed record ProfileResponse(Guid PlayerId, string DisplayName, string Kind, AvatarResponse Avatar, int GamesPlayed, int Wins, int Xp);

    /// <summary>
    /// One achievement of the catalog (STORY-058, DEC-063): how far the player is towards its target, and when it was
    /// unlocked (null while locked). An unlocked achievement shows its target as its progress.
    /// </summary>
    public sealed record AchievementResponse(string Id, string Name, string Description, int Progress, int Target, DateTimeOffset? UnlockedAt);

    /// <summary>Only these fields are editable; anything else in the body (ids, counters) is ignored.</summary>
    public sealed record UpdateProfileRequest(string? DisplayName, string? AvatarId);

    public static IEndpointRouteBuilder MapProfileEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var profile = endpoints.MapGroup("/api/profile").RequireAuthorization();

        profile.MapGet("", GetProfileAsync).WithName("GetProfile");
        profile.MapPatch("", UpdateProfileAsync).WithName("UpdateProfile");
        profile.MapGet("/achievements", GetAchievementsAsync).WithName("GetAchievements");
        profile.MapGet("/avatars", () => TypedResults.Ok(DefaultAvatar.All.Select(ToResponse).ToArray())).WithName("GetAvatars");

        return endpoints;
    }

    private static async Task<Results<Ok<ProfileResponse>, NotFound>> GetProfileAsync(
        ClaimsPrincipal user, ChogGamesDbContext db, CancellationToken cancellationToken)
    {
        var player = await FindCurrentPlayerAsync(user, db, cancellationToken);
        return player is null ? TypedResults.NotFound() : TypedResults.Ok(await ToResponseAsync(player, db, cancellationToken));
    }

    /// <summary>Every achievement of the catalog, in its order, for the caller only.</summary>
    private static async Task<Results<Ok<AchievementResponse[]>, NotFound>> GetAchievementsAsync(
        ClaimsPrincipal user, ChogGamesDbContext db, CancellationToken cancellationToken)
    {
        var playerId = Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var counts = await db.AchievementCountsAsync([playerId], cancellationToken);
        if (!counts.TryGetValue(playerId, out var totals))
        {
            return TypedResults.NotFound();
        }

        var unlocked = await db.PlayerAchievements.AsNoTracking()
            .Where(a => a.PlayerId == playerId)
            .ToDictionaryAsync(a => a.AchievementId, a => a.UnlockedAt, cancellationToken);

        return TypedResults.Ok(Achievement.All
            .Select(a => unlocked.TryGetValue(a.Id, out var at)
                ? new AchievementResponse(a.Id, a.Name, a.Description, a.Target, a.Target, at)
                : new AchievementResponse(a.Id, a.Name, a.Description, a.ProgressFrom(totals), a.Target, null))
            .ToArray());
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
        return TypedResults.Ok(await ToResponseAsync(player, db, cancellationToken));
    }

    private static Task<Player?> FindCurrentPlayerAsync(ClaimsPrincipal user, ChogGamesDbContext db, CancellationToken cancellationToken)
    {
        var playerId = Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);
        return db.Players.SingleOrDefaultAsync(p => p.Id == playerId, cancellationToken);
    }

    private static async Task<ProfileResponse> ToResponseAsync(Player player, ChogGamesDbContext db, CancellationToken cancellationToken)
    {
        var xp = await db.TotalXpAsync(player.Id, cancellationToken);
        return new ProfileResponse(
            player.Id, player.DisplayName, player.Kind.ToString(), ToResponse(AvatarOf(player.AvatarId)), player.GamesPlayed, player.Wins, xp);
    }

    /// <summary>An avatar retired from the built-in list falls back to the default rather than failing.</summary>
    internal static DefaultAvatar AvatarOf(string avatarId) => DefaultAvatar.TryFind(avatarId, out var found) ? found : DefaultAvatar.Classic;

    internal static AvatarResponse ToResponse(DefaultAvatar avatar) => new(avatar.Id, avatar.Name, avatar.Color);
}
