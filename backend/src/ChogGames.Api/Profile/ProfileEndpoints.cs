using System.Security.Claims;
using ChogGames.Core.Achievements;
using ChogGames.Core.Collectors;
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
    /// <param name="ImageUrl">
    /// The player's uploaded photo, shown instead of the built-in avatar while there is one (STORY-062, DEC-018); null
    /// otherwise and for the built-in avatars listed.
    /// </param>
    public sealed record AvatarResponse(string Id, string Name, string Color, string? ImageUrl = null);

    /// <summary>A cosmetic collector tier (STORY-060, DEC-065), all that other players see of someone's holdings.</summary>
    public sealed record CollectorBadgeResponse(string Id, string Name);

    /// <summary>The next collector tier and how many Chogs it takes.</summary>
    public sealed record CollectorMilestoneResponse(string Id, string Name, int MinChogs);

    /// <summary>
    /// The caller's own collector standing: Chogs held at the last gallery read, the tier, the next one and every tier
    /// from the lowest.
    /// </summary>
    public sealed record CollectorResponse(
        int ChogsHeld, CollectorBadgeResponse? Badge, CollectorMilestoneResponse? Next, CollectorMilestoneResponse[] Tiers);

    /// <param name="Xp">All the XP the player has earned (STORY-059, DEC-064).</param>
    /// <param name="Collector">The player's collector tier and progress (STORY-060, DEC-065).</param>
    public sealed record ProfileResponse(
        Guid PlayerId, string DisplayName, string Kind, AvatarResponse Avatar, int GamesPlayed, int Wins, int Xp, CollectorResponse Collector);

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
        profile.MapGet("/avatars", () => TypedResults.Ok(DefaultAvatar.All.Select(a => ToResponse(a)).ToArray())).WithName("GetAvatars");

        return endpoints;
    }

    private static async Task<Results<Ok<ProfileResponse>, NotFound>> GetProfileAsync(
        ClaimsPrincipal user, ChogGamesDbContext db, CancellationToken cancellationToken)
    {
        var player = await FindCurrentPlayerAsync(user, db, cancellationToken);
        return player is null ? TypedResults.NotFound() : TypedResults.Ok(await ToResponseAsync(player, db, cancellationToken));
    }

    /// <summary>The profile of <paramref name="playerId"/> as stored now, read untracked; null for an unknown player.</summary>
    internal static async Task<ProfileResponse?> FindProfileAsync(Guid playerId, ChogGamesDbContext db, CancellationToken cancellationToken) =>
        await db.Players.AsNoTracking().SingleOrDefaultAsync(p => p.Id == playerId, cancellationToken) is { } player
            ? await ToResponseAsync(player, db, cancellationToken)
            : null;

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
            player.Id,
            player.DisplayName,
            player.Kind.ToString(),
            ToResponse(AvatarOf(player.AvatarId), player.AvatarImageId),
            player.GamesPlayed,
            player.Wins,
            xp,
            new CollectorResponse(
                player.ChogsHeld,
                CollectorBadgeOf(player.ChogsHeld),
                CollectorTier.NextAfter(player.ChogsHeld) is { } next ? ToMilestone(next) : null,
                [.. CollectorTier.All.Select(ToMilestone)]));
    }

    private static CollectorMilestoneResponse ToMilestone(CollectorTier tier) => new(tier.Id, tier.Name, tier.MinChogs);

    /// <summary>The badge for <paramref name="chogsHeld"/> Chogs, or null below the first tier.</summary>
    internal static CollectorBadgeResponse? CollectorBadgeOf(int chogsHeld) =>
        CollectorTier.For(chogsHeld) is { } tier ? new CollectorBadgeResponse(tier.Id, tier.Name) : null;

    /// <summary>An avatar retired from the built-in list falls back to the default rather than failing.</summary>
    internal static DefaultAvatar AvatarOf(string avatarId) => DefaultAvatar.TryFind(avatarId, out var found) ? found : DefaultAvatar.Classic;

    /// <param name="imageId">The player's uploaded photo, if any; null for the built-in avatar alone.</param>
    internal static AvatarResponse ToResponse(DefaultAvatar avatar, Guid? imageId = null) =>
        new(avatar.Id, avatar.Name, avatar.Color, imageId is { } id ? AvatarImageEndpoints.UrlFor(id) : null);
}
