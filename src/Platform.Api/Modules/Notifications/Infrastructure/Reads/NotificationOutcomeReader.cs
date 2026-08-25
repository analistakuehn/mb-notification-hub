using Microsoft.EntityFrameworkCore;
using NotificationHub.Api.Modules.Notifications.Domain;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Persistence;
using NotificationHub.Api.Modules.Notifications.Integration.V1;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.Notifications.Infrastructure.Reads;

/// <summary>
/// The aggregate behind the outcome contract: three grouped reads over the
/// query context, one per question the window asks. Nothing is materialized
/// row by row, so the answer cannot carry a recipient, a destination or a
/// render even by accident.
/// </summary>
/// <remarks>
/// The window is mandatory and bounded because the three tables are
/// partitioned by month: an unbounded read would scan every partition the
/// database ever had, and a monthly report never needs more than the month it
/// reports.
/// </remarks>
internal sealed class NotificationOutcomeReader(NotificationsReadDbContext db) : INotificationOutcomeReport
{
    /// <summary>
    /// Longest window this read accepts. A year of monthly partitions is
    /// already far past what any recurring evidence asks for, and the ceiling
    /// is what keeps a mistyped window from turning into a full-history scan.
    /// </summary>
    internal static readonly TimeSpan MaxWindow = TimeSpan.FromDays(366);

    public async Task<Result<NotificationOutcomeSummary>> SummarizeAsync(
        DateTimeOffset fromInclusive,
        DateTimeOffset toExclusive,
        CancellationToken cancellationToken)
    {
        if (toExclusive <= fromInclusive)
        {
            return Result.ValidationError<NotificationOutcomeSummary>(
                "A janela do resumo precisa terminar depois de começar.");
        }

        if (toExclusive - fromInclusive > MaxWindow)
        {
            return Result.ValidationError<NotificationOutcomeSummary>(
                $"A janela do resumo precisa ser de no máximo {MaxWindow.TotalDays:F0} dias.");
        }

        List<ClassStatusRow> classRows = await db.Notifications
            .AsNoTracking()
            .Where(notification => notification.CreatedAt >= fromInclusive
                && notification.CreatedAt < toExclusive)
            .GroupBy(notification => new { notification.Class, notification.Status })
            .Select(group => new ClassStatusRow(group.Key.Class, group.Key.Status, group.LongCount()))
            .ToListAsync(cancellationToken);

        List<ChannelStatusRow> channelRows = await db.NotificationAttempts
            .AsNoTracking()
            .Where(attempt => attempt.CreatedAt >= fromInclusive && attempt.CreatedAt < toExclusive)
            .GroupBy(attempt => new { attempt.Channel, attempt.Status })
            .Select(group => new ChannelStatusRow(group.Key.Channel, group.Key.Status, group.LongCount()))
            .ToListAsync(cancellationToken);

        // A refusal without a stable reason is left out rather than bucketed:
        // the store guarantees one on a refusal, and inventing a label for a
        // row that carries none would put a fact in the evidence that no
        // column supports.
        List<ReasonRow> reasonRows = await db.PolicyEvaluations
            .AsNoTracking()
            .Where(evaluation => evaluation.EvaluatedAt >= fromInclusive
                && evaluation.EvaluatedAt < toExclusive
                && evaluation.Result == PolicyEvaluationResults.Reject
                && evaluation.Reason != null)
            .GroupBy(evaluation => evaluation.Reason!)
            .Select(group => new ReasonRow(group.Key, group.LongCount()))
            .ToListAsync(cancellationToken);

        return Result.Success(new NotificationOutcomeSummary
        {
            FromInclusive = fromInclusive,
            ToExclusive = toExclusive,
            VolumesByClass = ToClassVolumes(classRows),
            OutcomesByChannel = ToChannelOutcomes(channelRows),
            RejectionsByReason = [.. reasonRows
                .OrderBy(row => row.Reason, StringComparer.Ordinal)
                .Select(row => new NotificationRejectionCount { Reason = row.Reason, Count = row.Count })],
        });
    }

    /// <summary>
    /// Whether a provider on this channel ever reports what became of the
    /// message. Push is the channel where it never does: the plan treats an
    /// acceptance as the strongest signal precisely because no delivery report
    /// and no later lookup exist for it.
    /// </summary>
    private static string ConfirmationOf(string channel)
        => string.Equals(channel, AttemptDispatchWriter.PushChannel, StringComparison.Ordinal)
            ? DeliveryConfirmationSources.AcceptanceOnly
            : DeliveryConfirmationSources.ProviderFeedback;

    private static IReadOnlyList<NotificationClassVolume> ToClassVolumes(IReadOnlyList<ClassStatusRow> rows)
        => [.. rows
            .GroupBy(row => row.Class, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new NotificationClassVolume
            {
                Class = group.Key,
                Requested = group.Sum(row => row.Count),
                ByStatus = [.. group
                    .OrderBy(row => row.Status, StringComparer.Ordinal)
                    .Select(row => new NotificationStatusCount { Status = row.Status, Count = row.Count })],
            })];

    private static IReadOnlyList<NotificationChannelOutcome> ToChannelOutcomes(IReadOnlyList<ChannelStatusRow> rows)
        => [.. rows
            .GroupBy(row => row.Channel, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new NotificationChannelOutcome
            {
                Channel = group.Key,
                DeliveryConfirmation = ConfirmationOf(group.Key),
                Attempts = group.Sum(row => row.Count),
                AcceptedByProvider = Count(
                    group,
                    NotificationAttemptStatuses.Sent,
                    NotificationAttemptStatuses.Delivered,
                    NotificationAttemptStatuses.Read,
                    NotificationAttemptStatuses.Bounced),
                Delivered = Count(
                    group,
                    NotificationAttemptStatuses.Delivered,
                    NotificationAttemptStatuses.Read),
                Bounced = Count(group, NotificationAttemptStatuses.Bounced),
                Failed = Count(group, NotificationAttemptStatuses.Failed),
                Unknown = Count(group, NotificationAttemptStatuses.Unknown),
                Pending = Count(
                    group,
                    NotificationAttemptStatuses.Queued,
                    NotificationAttemptStatuses.Sending),
            })];

    private static long Count(IEnumerable<ChannelStatusRow> rows, params string[] statuses)
        => rows.Where(row => statuses.Contains(row.Status, StringComparer.Ordinal)).Sum(row => row.Count);

    private sealed record ClassStatusRow(string Class, string Status, long Count);

    private sealed record ChannelStatusRow(string Channel, string Status, long Count);

    private sealed record ReasonRow(string Reason, long Count);
}
