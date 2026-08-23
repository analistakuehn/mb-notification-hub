using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace NotificationHub.IntegrationTests.Dispatching;

/// <summary>
/// Captures every rendered log line of a worker composition, so a test can
/// prove that revealed addresses, tokens and rendered content never reach a
/// log.
/// </summary>
internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    public ConcurrentQueue<string> Lines { get; } = new();

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(Lines);

    public void Dispose()
    {
    }

    private sealed class CapturingLogger(ConcurrentQueue<string> lines) : ILogger
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
            lines.Enqueue(formatter(state, exception));
            if (exception is not null)
            {
                lines.Enqueue(exception.ToString());
            }
        }
    }
}
