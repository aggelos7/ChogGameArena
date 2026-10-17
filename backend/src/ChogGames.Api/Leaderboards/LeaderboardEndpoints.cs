using System.Security.Claims;
using System.Threading.RateLimiting;
using ChogGames.Api.Profile;
using ChogGames.Api.Rooms;
using ChogGames.Core.Games;
using ChogGames.Core.Progression;
using ChogGames.Infrastructure.Persistence;
using ChogGames.Infrastructure.Progression;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;

namespace ChogGames.Api.Leaderboards;

/// <summary>
/// XP leaderboards (STORY-059, DEC-064): overall or one game, all-time or the current season. Players appear by display
/// name and avatar only; no player id or wallet is ever returned, and the viewer is marked by the session.
/// </summary>
public static class LeaderboardEndpoints
{
    public const string RateLimitPolicy = "leaderboards";

    /// <summary>The <c>game</c> value for the board across every game.</summary>
    public const string AllGames = "all";

    public const string AllTime = "all-time";

    public const string CurrentSeason = "season";

    /// <param name="Collector">The player's cosmetic collector badge, null below the first tier (STORY-060, DEC-065).</param>
    public sealed record LeaderboardEntryResponse(
        int Place, string DisplayName, ProfileEndpoints.AvatarResponse Avatar, ProfileEndpoints.CollectorBadgeResponse? Collector, int Xp, int Wins, bool IsYou);

    public sealed record SeasonResponse(DateTimeOffset StartsAt, DateTimeOffset EndsAt);

    /// <param name="Season">The season counted; null for an all-time board.</param>
    /// <param name="Entries">The top places, in order.</param>
    /// <param name="You">The viewer's place, also when it's below the top; null while they have no XP on this board.</param>
    public sealed record LeaderboardResponse(
        string Game, string Period, SeasonResponse? Season, LeaderboardEntryResponse[] Entries, LeaderboardEntryResponse? You);

    /// <summary>Stable error codes; clients rely on these, never on the message text.</summary>
    public static class ErrorCodes
    {
        public const string UnknownGame = RoomEndpoints.ErrorCodes.UnknownGame;
        public const string UnknownPeriod = "unknown_period";
    }

    public static IServiceCollection AddLeaderboards(this IServiceCollection services)
    {
        services.AddRateLimiter(options => options.AddPolicy(RateLimitPolicy, context =>
        {
            // Per player, with the rooms' budget: every request ranks every player.
            var permitLimit = context.RequestServices.GetRequiredService<IOptions<RoomOptions>>().Value.RequestsPerMinutePerPlayer;
            return RateLimitPartition.GetFixedWindowLimiter(
                context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "anonymous",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = permitLimit, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 });
        }));

        return services;
    }

    public static IEndpointRouteBuilder MapLeaderboardEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/leaderboards", GetLeaderboardAsync)
            .RequireAuthorization()
            .RequireRateLimiting(RateLimitPolicy)
            .WithName("GetLeaderboard");

        return endpoints;
    }

    /// <summary><c>GET /api/leaderboards?game=all|&lt;game type&gt;&amp;period=all-time|season</c>; both default to the first.</summary>
    private static async Task<Results<Ok<LeaderboardResponse>, ProblemHttpResult>> GetLeaderboardAsync(
        string? game, string? period, ClaimsPrincipal user, ChogGamesDbContext db, IGameCatalog catalog, TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        game ??= AllGames;
        period ??= AllTime;
        if (game != AllGames && catalog.Find(game) is null)
        {
            return Problem(ErrorCodes.UnknownGame, "This game does not exist.");
        }

        if (period is not (AllTime or CurrentSeason))
        {
            return Problem(ErrorCodes.UnknownPeriod, $"The period must be {AllTime} or {CurrentSeason}.");
        }

        var season = period == CurrentSeason ? Season.Containing(timeProvider.GetUtcNow()) : null;
        var viewerId = Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var standings = await db.LeaderboardAsync(game == AllGames ? null : game, season, viewerId, cancellationToken);

        LeaderboardEntryResponse ToResponse(LeaderboardEntry entry) => new(
            entry.Place,
            entry.DisplayName,
            ProfileEndpoints.ToResponse(ProfileEndpoints.AvatarOf(entry.AvatarId)),
            ProfileEndpoints.CollectorBadgeOf(entry.ChogsHeld),
            entry.Xp,
            entry.Wins,
            entry.PlayerId == viewerId);

        return TypedResults.Ok(new LeaderboardResponse(
            game,
            period,
            season is null ? null : new SeasonResponse(season.StartsAt, season.EndsAt),
            [.. standings.Top.Select(ToResponse)],
            standings.Viewer is { } viewer ? ToResponse(viewer) : null));
    }

    private static ProblemHttpResult Problem(string code, string detail) =>
        TypedResults.Problem(detail, statusCode: StatusCodes.Status400BadRequest, extensions: new Dictionary<string, object?> { ["code"] = code });
}
