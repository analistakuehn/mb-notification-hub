using System.Collections.Concurrent;
using System.Data.Common;
using Microsoft.Extensions.DependencyInjection;
using NotificationHub.Api.Modules.Audit.Integration.V1;

namespace NotificationHub.IntegrationTests.Notifications;

/// <summary>Wraps the composed audit trail without naming its implementation.</summary>
internal static class AuditTrailDecoration
{
    internal static Action<IServiceCollection> ProbeAppendOrder(Action<AppendOrderProbe> capture)
        => services =>
        {
            ServiceDescriptor original = services.Last(
                descriptor => descriptor.ServiceType == typeof(IAuditTrail));
            services.Remove(original);
            services.AddSingleton<IAuditTrail>(provider =>
            {
                var inner = (IAuditTrail)ActivatorUtilities.CreateInstance(
                    provider, original.ImplementationType!);
                var probe = new AppendOrderProbe(inner);
                capture(probe);
                return probe;
            });
        };
}

/// <summary>
/// Observes, from inside the writer's own transaction, whether the outgoing
/// integration event was already appended when the audit trail was called.
///
/// The order is load bearing, not cosmetic: the audit append takes the chain
/// lock of the monthly partition and holds it until the transaction ends, so
/// anything queued after it widens the window every concurrent ingestion waits
/// on. Counting the outbox rows visible to the same open transaction is the
/// only oracle that answers the question without comparing two clocks.
/// </summary>
internal sealed class AppendOrderProbe(IAuditTrail inner) : IAuditTrail
{
    private const string CountSql = """
        SELECT count(*) FROM platform.outbox
        WHERE transport = 'kafka' AND payload::text LIKE @pattern
        """;

    private readonly ConcurrentDictionary<string, int> _busRowsBeforeAudit = new(StringComparer.Ordinal);

    /// <summary>How many bus rows for one entity were already appended when the audit call ran.</summary>
    public int BusRowsBeforeAuditOf(string entityId)
        => _busRowsBeforeAudit.TryGetValue(entityId, out var count) ? count : -1;

    public async Task AppendAsync(DbTransaction transaction, AuditEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(entry);

        _busRowsBeforeAudit[entry.EntityId] =
            await CountBusRowsAsync(transaction, entry.EntityId, cancellationToken);
        await inner.AppendAsync(transaction, entry, cancellationToken);
    }

    public Task RecordApprovalAsync(
        DbTransaction transaction,
        ApprovalGrant grant,
        CancellationToken cancellationToken)
        => inner.RecordApprovalAsync(transaction, grant, cancellationToken);

    private static async Task<int> CountBusRowsAsync(
        DbTransaction transaction,
        string entityId,
        CancellationToken cancellationToken)
    {
        DbConnection connection = transaction.Connection
            ?? throw new InvalidOperationException("A sonda de ordem exige uma transação com conexão aberta.");
        await using DbCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = CountSql;
        DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = "pattern";
        parameter.Value = $"%{entityId}%";
        command.Parameters.Add(parameter);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(result, System.Globalization.CultureInfo.InvariantCulture);
    }
}
