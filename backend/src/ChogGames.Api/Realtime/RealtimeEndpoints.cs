using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using ChogGames.Api.Rooms;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace ChogGames.Api.Realtime;

/// <summary>SignalR composition (STORY-022): the room hub over WebSockets, behind the session and the room rate limits.</summary>
public static class RealtimeEndpoints
{
    public static IServiceCollection AddRealtime(this IServiceCollection services)
    {
        services.AddSignalR()
            .AddHubOptions<RoomHub>(options => options.AddFilter<HubRateLimitFilter>())
            // Snapshots read the same over the hub as over REST (DEC-028).
            .AddJsonProtocol(options => options.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        services.AddSingleton<HubRateLimitFilter>();
        services.AddSingleton<RoomConnectionRegistry>();
        services.AddSingleton<RoomNotifier>();
        return services;
    }

    public static IEndpointRouteBuilder MapRealtimeEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // WebSockets only (HTTPS/WSS is required, requirements §Staging): each long-polling request would count
        // against the rate limit. Connecting counts as a room request.
        endpoints.MapHub<RoomHub>(RoomHub.Path, options => options.Transports = HttpTransportType.WebSockets)
            .RequireAuthorization()
            .RequireRateLimiting(RoomEndpoints.RateLimitPolicy);
        return endpoints;
    }
}

/// <summary>
/// Limits hub calls per player, as <c>Rooms:RequestsPerMinutePerPlayer</c> limits room requests: HTTP rate limiting
/// only sees the connection being opened, not the calls made over it.
/// </summary>
public sealed class HubRateLimitFilter : IHubFilter, IDisposable
{
    private readonly PartitionedRateLimiter<string> _limiter;

    public HubRateLimitFilter(IOptions<RoomOptions> options)
    {
        var permitLimit = options.Value.RequestsPerMinutePerPlayer;
        _limiter = PartitionedRateLimiter.Create<string, string>(playerId => RateLimitPartition.GetFixedWindowLimiter(
            playerId,
            _ => new FixedWindowRateLimiterOptions { PermitLimit = permitLimit, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    }

    public async ValueTask<object?> InvokeMethodAsync(HubInvocationContext invocationContext, Func<HubInvocationContext, ValueTask<object?>> next)
    {
        using var lease = _limiter.AttemptAcquire(invocationContext.Context.UserIdentifier ?? "anonymous");
        if (!lease.IsAcquired)
        {
            throw new HubException(RoomHub.ErrorCodes.RateLimited);
        }

        return await next(invocationContext);
    }

    public void Dispose() => _limiter.Dispose();
}
