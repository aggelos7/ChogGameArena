using ChogGames.Infrastructure.Monitoring;
using ChogGames.Infrastructure.Persistence;

namespace ChogGames.Api.Monitoring;

public static class MonitoringEndpoints
{
    /// <summary>
    /// Outside <c>/api</c>, so nginx never forwards it: only the host and its Compose network can read it (DEC-052).
    /// </summary>
    public const string MetricsPath = "/metrics";

    public static IServiceCollection AddMonitoring(this IServiceCollection services)
    {
        services.AddSingleton<PrometheusMetrics>();
        return services;
    }

    /// <summary>Request correlation runs first, so every later log line of the request carries its id.</summary>
    public static IApplicationBuilder UseRequestCorrelation(this IApplicationBuilder app) =>
        app.UseMiddleware<RequestCorrelationMiddleware>();

    public static IEndpointRouteBuilder MapMonitoringEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Resolved now, so the listener sees the request and connection meters from the first request on.
        endpoints.ServiceProvider.GetRequiredService<PrometheusMetrics>();

        endpoints.MapGet(MetricsPath, async (HttpContext context, PrometheusMetrics prometheus, ChogGamesMetrics metrics, ChogGamesDbContext db) =>
            {
                // A database outage leaves the open-room gauge out rather than stale; readiness reports the outage.
                try
                {
                    await metrics.RefreshOpenRoomsAsync(db, context.RequestAborted);
                }
                catch (Exception error) when (!context.RequestAborted.IsCancellationRequested)
                {
                    context.RequestServices.GetRequiredService<ILogger<PrometheusMetrics>>()
                        .LogWarning(error, "Open rooms could not be counted for the metrics.");
                }

                context.Response.Headers.CacheControl = "no-store";
                return Results.Text(prometheus.Collect(), PrometheusMetrics.ContentType);
            })
            .ExcludeFromDescription();

        return endpoints;
    }
}
