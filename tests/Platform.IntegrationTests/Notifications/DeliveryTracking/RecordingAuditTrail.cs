using System.Data.Common;
using NotificationHub.Api.Modules.Audit.Integration.V1;

namespace NotificationHub.IntegrationTests.Notifications.DeliveryTracking;

/// <summary>
/// Counts every audit append that runs while it is composed, so a test can
/// prove that a path never touched the trail.
///
/// The count is the oracle and not a timing observation: the append takes the
/// chain lock of the trail's monthly partition and holds it until the caller's
/// transaction ends, so one append inside the request that answers a provider
/// callback would serialize every callback against this hub's own ingestion.
/// The provider decides the callback rate, so that is a defect no load test
/// would forgive.
/// </summary>
internal sealed class RecordingAuditTrail : IAuditTrail
{
    private int _appends;

    public int Appends => Volatile.Read(ref _appends);

    /// <summary>Actions the trail was asked to record, in call order.</summary>
    public List<string> Actions { get; } = [];

    public Task AppendAsync(DbTransaction transaction, AuditEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        Interlocked.Increment(ref _appends);
        lock (Actions) Actions.Add(entry.Action);

        return Task.CompletedTask;
    }

    public Task RecordApprovalAsync(
        DbTransaction transaction,
        ApprovalGrant grant,
        CancellationToken cancellationToken)
        => Task.CompletedTask;
}
