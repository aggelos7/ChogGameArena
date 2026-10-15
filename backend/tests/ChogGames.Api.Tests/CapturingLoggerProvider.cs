using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ChogGames.Api.Tests;

/// <summary>
/// Keeps every log entry the host writes, at every level, with its message, structured values, scope values and
/// exception, so tests can check what reaches the logs. Register it with <see cref="AddTo"/>.
/// </summary>
public sealed class CapturingLoggerProvider : ILoggerProvider, ISupportExternalScope
{
    public sealed record Entry(string Category, LogLevel Level, string Message, IReadOnlyList<KeyValuePair<string, object?>> Values,
        IReadOnlyList<KeyValuePair<string, object?>> Scopes, string? Exception)
    {
        /// <summary>Everything this entry wrote, as one string to search.</summary>
        public string Text => string.Join('\n', new[] { Category, Message, Exception ?? "" }
            .Concat(Values.Concat(Scopes).Select(v => $"{v.Key}={v.Value}")));
    }

    private readonly ConcurrentQueue<Entry> _entries = new();
    private IExternalScopeProvider _scopes = new LoggerExternalScopeProvider();

    public IReadOnlyCollection<Entry> Entries => _entries;

    /// <summary>Captures everything, whatever the configured log levels.</summary>
    public void AddTo(IServiceCollection services) =>
        services.AddLogging(logging =>
        {
            logging.AddProvider(this);
            logging.AddFilter<CapturingLoggerProvider>(null, LogLevel.Trace);
        });

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;

    public void Dispose()
    {
    }

    private sealed class Logger(CapturingLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => provider._scopes.Push(state);

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var values = state as IEnumerable<KeyValuePair<string, object?>> ?? [];
            var scopes = new List<KeyValuePair<string, object?>>();
            provider._scopes.ForEachScope((scope, list) =>
            {
                if (scope is IEnumerable<KeyValuePair<string, object?>> pairs)
                {
                    list.AddRange(pairs);
                }
                else if (scope is IEnumerable<KeyValuePair<string, object>> objectPairs)
                {
                    list.AddRange(objectPairs.Select(p => new KeyValuePair<string, object?>(p.Key, p.Value)));
                }
                else
                {
                    list.Add(new KeyValuePair<string, object?>("Scope", scope?.ToString()));
                }
            }, scopes);

            provider._entries.Enqueue(new Entry(category, logLevel, formatter(state, exception), [.. values], scopes, exception?.ToString()));
        }
    }
}
