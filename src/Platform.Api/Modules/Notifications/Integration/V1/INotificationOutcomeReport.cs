using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.Notifications.Integration.V1;

/// <summary>
/// Aggregate read surface of this module for a periodic evidence composer. It
/// answers with counts and never with rows: no recipient identity, no contact
/// value, no rendered content and no variable ever crosses it, which is what
/// lets an answer be archived and read by people who have no right to any of
/// those.
/// </summary>
/// <remarks>
/// The aggregation happens inside this module on purpose. What a status means,
/// which of them count as a provider taking responsibility, and which channel
/// can never report a delivery are facts of this context; a consumer that
/// grouped raw rows would be reimplementing them and would drift the first
/// time a status is added.
/// </remarks>
public interface INotificationOutcomeReport
{
    /// <summary>
    /// Summarizes the notifications created and the attempts queued inside the
    /// window. An inverted or unbounded window fails as a validation error.
    /// </summary>
    Task<Result<NotificationOutcomeSummary>> SummarizeAsync(
        DateTimeOffset fromInclusive,
        DateTimeOffset toExclusive,
        CancellationToken cancellationToken);
}
