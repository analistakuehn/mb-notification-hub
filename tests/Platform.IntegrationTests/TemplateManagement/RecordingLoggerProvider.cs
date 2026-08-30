using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace NotificationHub.IntegrationTests.TemplateManagement;

/// <summary>One log line of the host, with the event id a rendered line drops.</summary>
internal sealed record RecordedEvent(LogLevel Level, EventId EventId, string Message);

/// <summary>
/// Captures the log lines of a host along with their event ids, so a test can
/// name the event a refusal is supposed to leave instead of matching prose.
/// </summary>
internal sealed class RecordingLoggerProvider : ILoggerProvider
{
    public ConcurrentQueue<RecordedEvent> Events { get; } = new();

    public ILogger CreateLogger(string categoryName) => new RecordingLogger(Events);

    public void Dispose()
    {
    }

    private sealed class RecordingLogger(ConcurrentQueue<RecordedEvent> events) : ILogger
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
            // Formatted here and not later: the generated call sites write their
            // values into a state object they reuse per thread and clear as soon
            // as this returns.
            => events.Enqueue(new RecordedEvent(logLevel, eventId, formatter(state, exception)));
    }
}
