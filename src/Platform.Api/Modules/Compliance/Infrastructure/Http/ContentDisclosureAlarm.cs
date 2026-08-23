using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace NotificationHub.Api.Modules.Compliance.Infrastructure.Http;

/// <summary>
/// Counts how often one principal opened stored content and raises a warning
/// once the count crosses the configured volume inside the window. The counter
/// is in process, like the rate limiter beside it: a replica-local count is
/// enough to notice a sweep, and a shared counter would put the disclosure path
/// behind a network hop it does not need.
/// </summary>
internal sealed class ContentDisclosureAlarm(
    IOptions<ContentDisclosureAlarmOptions> options,
    TimeProvider timeProvider,
    ILogger<ContentDisclosureAlarm> logger)
{
    private readonly ConcurrentDictionary<string, Window> _windows = new(StringComparer.Ordinal);

    internal void RecordDisclosure(string principal)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(principal);
        ContentDisclosureAlarmOptions settings = options.Value;
        DateTimeOffset now = timeProvider.GetUtcNow();
        var span = TimeSpan.FromMinutes(settings.WindowMinutes);

        Window window = _windows.AddOrUpdate(
            principal,
            _ => new Window(now, 1),
            (_, current) => now - current.StartedAt >= span
                ? new Window(now, 1)
                : current with { Disclosures = current.Disclosures + 1 });

        // The alarm fires on the crossing only: a principal parked above the
        // threshold would otherwise log once per read and drown the signal.
        if (window.Disclosures == settings.Threshold)
        {
            logger.ContentDisclosureVolumeReached(principal, window.Disclosures, settings.WindowMinutes);
        }
    }

    private sealed record Window(DateTimeOffset StartedAt, int Disclosures);
}
