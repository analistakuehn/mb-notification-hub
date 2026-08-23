using System.Data.Common;

namespace NotificationHub.Api.Infrastructure.Messaging.Consuming;

/// <summary>
/// Transactional dedupe mark of the consumer side. The mark executes on the
/// caller's open transaction, mirroring the outbox writer dialect: the effect
/// and its mark commit together or not at all, which is what makes redelivery
/// after a commit detectable and redelivery after a rollback re-processable.
/// </summary>
public interface IProcessedMessageStore
{
    /// <summary>
    /// Marks <paramref name="messageId"/> as processed by
    /// <paramref name="consumer"/> inside <paramref name="transaction"/>.
    /// Returns false when the mark already existed: the caller saw a
    /// duplicate and must produce no effect.
    /// </summary>
    Task<bool> TryMarkAsync(
        DbTransaction transaction,
        string messageId,
        string consumer,
        CancellationToken cancellationToken);
}
