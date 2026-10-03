using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Huishoudplanner.Integration.Tests.Fixtures;

public sealed record LogEntry(LogLevel Level, string Category, string Message);

/// <summary>An <see cref="ILoggerProvider"/> that keeps what was logged, so a test can assert on it.</summary>
public sealed class LogCollector : ILoggerProvider
{
    private readonly ConcurrentQueue<LogEntry> entries = new();

    public IReadOnlyList<LogEntry> Entries => [.. entries];

    public ILogger CreateLogger(string categoryName) => new CollectingLogger(categoryName, entries);

    public void Dispose()
    {
    }

    private sealed class CollectingLogger(string category, ConcurrentQueue<LogEntry> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            entries.Enqueue(new LogEntry(logLevel, category, formatter(state, exception)));
        }
    }
}
