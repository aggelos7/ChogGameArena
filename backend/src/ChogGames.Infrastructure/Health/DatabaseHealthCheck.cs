using ChogGames.Infrastructure.Persistence;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace ChogGames.Infrastructure.Health;

/// <summary>Readiness check: PostgreSQL accepts connections for the configured database.</summary>
public sealed class DatabaseHealthCheck(ChogGamesDbContext dbContext) : IHealthCheck
{
    public const string Name = "postgresql";

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            return await dbContext.Database.CanConnectAsync(cancellationToken)
                ? HealthCheckResult.Healthy()
                : new HealthCheckResult(context.Registration.FailureStatus, "Database is unreachable.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return new HealthCheckResult(context.Registration.FailureStatus, "Database is unreachable.", exception);
        }
    }
}
