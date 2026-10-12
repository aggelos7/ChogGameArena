using ChogGames.Api.Realtime;
using ChogGames.Infrastructure.Matches;
using Microsoft.Extensions.Options;

namespace ChogGames.Api.Matches;

/// <summary>
/// The in-process turn scheduler (STORY-025, ENG-007, DEC-027): every <see cref="MatchOptions.TimeoutCheckInterval"/> it
/// times out the turns that ran out and tells each match's members over SignalR. A player who forfeited by missing turns
/// hears of it and then stops getting the match's updates, as when leaving. Single instance, like the hub (DEC-024).
/// </summary>
public sealed class TurnTimeoutWorker(IServiceScopeFactory scopes, IOptions<MatchOptions> options, ILogger<TurnTimeoutWorker> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Value.TimeoutCheckInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var timedOut = await scope.ServiceProvider.GetRequiredService<TurnTimeoutService>().TimeOutOverdueTurnsAsync(stoppingToken);
                var notifier = scope.ServiceProvider.GetRequiredService<RoomNotifier>();
                foreach (var turn in timedOut)
                {
                    await notifier.RoomChangedAsync(turn.Code, turn.ForfeitedPlayerId);
                }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                // The database may be briefly unavailable; the next tick tries again.
                logger.LogError(error, "Overdue turns could not be checked.");
            }
        }
    }
}
