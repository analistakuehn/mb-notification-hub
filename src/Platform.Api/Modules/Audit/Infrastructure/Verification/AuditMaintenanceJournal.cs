using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Storage;
using NotificationHub.Api.Modules.Audit.Domain;
using NotificationHub.Api.Modules.Audit.Infrastructure.Persistence;
using NotificationHub.Api.Modules.Audit.Integration.V1;

namespace NotificationHub.Api.Modules.Audit.Infrastructure.Verification;

/// <summary>
/// How maintenance rounds leave a durable record: through the same
/// transactional trail every governed effect uses. Verification results,
/// exports and partition closings are governed effects of this module, so they
/// are audited exactly like anything else, and an auditor finds them by
/// reading the trail rather than by trusting a log pipeline.
/// </summary>
internal sealed class AuditMaintenanceJournal(AuditDbContext db, IAuditTrail trail, TimeProvider timeProvider)
{
    /// <summary>Actor identity of every effect this module's maintenance produces.</summary>
    internal const string ActorId = "audit-maintenance";

    public async Task RecordAsync(
        string action,
        string partitionName,
        IReadOnlyList<(string Name, object? Value)> details,
        CancellationToken cancellationToken)
    {
        await using IDbContextTransaction transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await trail.AppendAsync(
            transaction.GetDbTransaction(),
            new AuditEntry
            {
                ActorType = AuditActorTypes.System,
                ActorId = ActorId,
                Action = action,
                EntityType = AuditEntityTypes.AuditPartition,
                EntityId = partitionName,
                DetailsJson = Details(details),
                OccurredAt = timeProvider.GetUtcNow(),
            },
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// Compact JSON with the evidence of the round. Values are written with
    /// the JSON writer rather than composed as text, so a name or a hash never
    /// escapes the document it belongs to.
    /// </summary>
    internal static string Details(IReadOnlyList<(string Name, object? Value)> values)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (Utf8JsonWriter writer = CanonicalJson.CreateWriter(buffer))
        {
            writer.WriteStartObject();
            foreach ((var name, var value) in values)
            {
                switch (value)
                {
                    case null:
                        writer.WriteNull(name);
                        break;
                    case string text:
                        writer.WriteString(name, text);
                        break;
                    case bool flag:
                        writer.WriteBoolean(name, flag);
                        break;
                    case int number:
                        writer.WriteNumber(name, number);
                        break;
                    case long number:
                        writer.WriteNumber(name, number);
                        break;
                    default:
                        writer.WriteString(name, Convert.ToString(value, CultureInfo.InvariantCulture));
                        break;
                }
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
