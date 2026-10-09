using System.Text.Json;
using ChogGames.Infrastructure.Configuration;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace ChogGames.Api.Health;

public static class HealthEndpoints
{
    public const string LivenessPath = "/health/live";
    public const string ReadinessPath = "/health/ready";

    /// <summary>
    /// Liveness runs no dependency checks, so a database outage never restarts the process.
    /// Readiness runs the checks tagged <see cref="ConfigurationServiceCollectionExtensions.ReadinessTag"/>
    /// and returns 503 when any of them fails.
    /// </summary>
    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapHealthChecks(LivenessPath, new HealthCheckOptions
        {
            Predicate = _ => false,
            ResponseWriter = WriteResponse,
        });

        endpoints.MapHealthChecks(ReadinessPath, new HealthCheckOptions
        {
            Predicate = registration => registration.Tags.Contains(ConfigurationServiceCollectionExtensions.ReadinessTag),
            ResponseWriter = WriteResponse,
        });

        return endpoints;
    }

    // Reports only names, statuses and durations: exception details and descriptions could
    // expose hosts or credentials, so they are left to the server logs.
    private static Task WriteResponse(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json";
        context.Response.Headers.CacheControl = "no-store";

        var body = new
        {
            status = report.Status.ToString(),
            checks = report.Entries.Select(entry => new
            {
                name = entry.Key,
                status = entry.Value.Status.ToString(),
                durationMs = Math.Round(entry.Value.Duration.TotalMilliseconds),
            }),
        };

        return context.Response.WriteAsync(JsonSerializer.Serialize(body, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }
}
