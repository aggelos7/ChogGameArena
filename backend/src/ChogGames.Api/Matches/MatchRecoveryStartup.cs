using ChogGames.Infrastructure.Matches;

namespace ChogGames.Api.Matches;

/// <summary>
/// The restart policy (STORY-028, ENG-014, DEC-030): when the API starts, active matches resume from their stored state
/// and each current player gets a full new turn. Hosted services start before the server takes requests, and this one is
/// registered before <see cref="TurnTimeoutWorker"/>, so no command or timeout sees a match before it is resumed. If the
/// database can't be reached the API still starts, and those matches' turns time out as usual (DEC-027).
/// </summary>
public sealed class MatchRecoveryStartup(IServiceScopeFactory scopes, ILogger<MatchRecoveryStartup> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<MatchRecoveryService>().ResumeActiveMatchesAsync(cancellationToken);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            logger.LogError(error, "Active matches could not be resumed after the restart.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
