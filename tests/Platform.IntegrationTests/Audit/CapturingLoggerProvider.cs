using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace NotificationHub.IntegrationTests.Audit;

/// <summary>
/// Collects rendered log messages of a composed provider, so a test can assert
/// which gated step reported itself without reaching into the job's internals.
/// </summary>
internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _messages = new();

    public IReadOnlyCollection<string> Messages => [.. _messages];

    public ILogger CreateLogger(string categoryName) => new Collecting(_messages);

    public void Dispose()
    {
    }

    private sealed class Collecting(ConcurrentQueue<string> messages) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            messages.Enqueue(formatter(state, exception));
        }
    }
}
