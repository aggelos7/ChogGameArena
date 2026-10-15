using System.Data.Common;
using System.Runtime.CompilerServices;
using ChogGames.Core.Rooms;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ChogGames.Infrastructure.Monitoring;

/// <summary>
/// Counts room status changes (created, cancelled, started, completed, abandoned) once they are committed (STORY-048).
/// Every transition is saved through the DbContext, wherever in the code it happens, so counting here misses none.
/// Changes saved inside an explicit transaction wait for its commit, so a rolled-back change is never counted.
/// </summary>
public sealed class RoomLifecycleMetricsInterceptor(ChogGamesMetrics metrics) : SaveChangesInterceptor, IDbTransactionInterceptor
{
    private sealed record Change(string GameType, RoomStatus? From, RoomStatus To);

    private sealed class State
    {
        public List<Change> Saving { get; } = [];
        public List<Change> AwaitingCommit { get; } = [];
    }

    private readonly ConditionalWeakTable<DbContext, State> _states = new();

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Capture(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Capture(eventData.Context);
        return ValueTask.FromResult(result);
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        Saved(eventData.Context);
        return result;
    }

    public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        Saved(eventData.Context);
        return ValueTask.FromResult(result);
    }

    public override void SaveChangesFailed(DbContextErrorEventData eventData) => StateOf(eventData.Context)?.Saving.Clear();

    public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        StateOf(eventData.Context)?.Saving.Clear();
        return Task.CompletedTask;
    }

    // A new transaction starts clean: changes left by one that was disposed without a commit were never committed.
    public DbTransaction TransactionStarted(DbConnection connection, TransactionEndEventData eventData, DbTransaction result)
    {
        StateOf(eventData.Context)?.AwaitingCommit.Clear();
        return result;
    }

    public ValueTask<DbTransaction> TransactionStartedAsync(
        DbConnection connection, TransactionEndEventData eventData, DbTransaction result, CancellationToken cancellationToken = default)
    {
        StateOf(eventData.Context)?.AwaitingCommit.Clear();
        return ValueTask.FromResult(result);
    }

    public void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData) => Committed(eventData.Context);

    public Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        Committed(eventData.Context);
        return Task.CompletedTask;
    }

    public void TransactionRolledBack(DbTransaction transaction, TransactionEndEventData eventData) =>
        StateOf(eventData.Context)?.AwaitingCommit.Clear();

    public Task TransactionRolledBackAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        StateOf(eventData.Context)?.AwaitingCommit.Clear();
        return Task.CompletedTask;
    }

    public void TransactionFailed(DbTransaction transaction, TransactionErrorEventData eventData) =>
        StateOf(eventData.Context)?.AwaitingCommit.Clear();

    public Task TransactionFailedAsync(DbTransaction transaction, TransactionErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        StateOf(eventData.Context)?.AwaitingCommit.Clear();
        return Task.CompletedTask;
    }

    private State? StateOf(DbContext? context) => context is not null && _states.TryGetValue(context, out var state) ? state : null;

    private void Capture(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        var changes = context.ChangeTracker.Entries<GameRoom>().Select(ChangeOf).OfType<Change>().ToList();
        var state = _states.GetOrCreateValue(context);
        state.Saving.Clear();
        state.Saving.AddRange(changes);
    }

    private static Change? ChangeOf(EntityEntry<GameRoom> entry)
    {
        if (entry.State == EntityState.Added)
        {
            return new Change(entry.Entity.GameType, null, entry.Entity.Status);
        }

        var status = entry.Property(r => r.Status);
        return entry.State == EntityState.Modified && status.IsModified && status.OriginalValue != status.CurrentValue
            ? new Change(entry.Entity.GameType, status.OriginalValue, status.CurrentValue)
            : null;
    }

    private void Saved(DbContext? context)
    {
        var state = StateOf(context);
        if (state is null || state.Saving.Count == 0)
        {
            return;
        }

        if (context!.Database.CurrentTransaction is null)
        {
            Record(state.Saving);
        }
        else
        {
            state.AwaitingCommit.AddRange(state.Saving);
        }

        state.Saving.Clear();
    }

    private void Committed(DbContext? context)
    {
        var state = StateOf(context);
        if (state is not null)
        {
            Record(state.AwaitingCommit);
            state.AwaitingCommit.Clear();
        }
    }

    private void Record(IEnumerable<Change> changes)
    {
        foreach (var change in changes)
        {
            metrics.RoomChanged(change.GameType, change.From, change.To);
        }
    }
}
