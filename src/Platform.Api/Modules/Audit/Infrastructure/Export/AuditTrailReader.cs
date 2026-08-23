using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NotificationHub.Api.Infrastructure.Partitioning;
using NotificationHub.Api.Modules.Audit.Domain;
using NotificationHub.Api.Modules.Audit.Infrastructure.Persistence;
using NotificationHub.Api.Modules.Audit.Integration.V1;

namespace NotificationHub.Api.Modules.Audit.Infrastructure.Export;

/// <summary>
/// One stored row of the trail, with the chain columns exactly as the store
/// holds them. The canonical text is read as text on purpose: it carries the
/// bytes that were hashed, and any reserialization of the details column would
/// produce a different document with the same meaning and a different hash.
/// </summary>
internal sealed record AuditTrailRow(
    Guid Id,
    long Seq,
    DateTimeOffset OccurredAt,
    string ActorType,
    string ActorId,
    string? Application,
    string Action,
    string EntityType,
    string EntityId,
    string DetailsJson,
    string? Canonical,
    byte[]? PrevHash,
    byte[]? Hash)
{
    /// <summary>True for rows written before the chain existed; nothing vouches for them but the export.</summary>
    public bool IsUnchained => Canonical is null;

    /// <summary>
    /// The queryable column that no longer agrees with the canonical text this
    /// row was hashed from, or null when they still describe the same event.
    /// </summary>
    /// <remarks>
    /// The chain covers the canonical text, so editing a column beside it
    /// leaves every hash valid while every query returns the edited value.
    /// Comparing the two closes that gap. The comparison is limited to the
    /// scalar columns on purpose: the details column is stored as jsonb and
    /// the store re-serializes it on read, so an exact comparison there would
    /// raise integrity alarms about formatting.
    /// </remarks>
    public string? CanonicalDrift()
    {
        if (Canonical is null)
        {
            return null;
        }

        using JsonDocument document = JsonDocument.Parse(Canonical);
        JsonElement root = document.RootElement;
        (string Field, bool Agrees)[] comparisons =
        [
            ("id", Text(root, "id") == Id.ToString("D")),
            ("seq", root.GetProperty("seq").GetInt64() == Seq),
            ("action", Text(root, "action") == Action),
            ("actor_type", Text(root, "actorType") == ActorType),
            ("actor_id", Text(root, "actorId") == ActorId),
            ("application", Text(root, "application") == Application),
            ("entity_type", Text(root, "entityType") == EntityType),
            ("entity_id", Text(root, "entityId") == EntityId),
            ("occurred_at", Text(root, "occurredAt") == FormattedOccurredAt()),
        ];

        return Array.Find(comparisons, comparison => !comparison.Agrees).Field;
    }

    private string FormattedOccurredAt()
        => AuditChain.TruncateToMicroseconds(OccurredAt).UtcDateTime
            .ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture);

    private static string? Text(JsonElement root, string name)
        => root.TryGetProperty(name, out JsonElement value) ? value.GetString() : null;

    /// <summary>
    /// Canonical text of a pre-chain row, produced at export time with the
    /// canonicalization the chain uses. It is evidence of content, never a
    /// retroactive chain link: no hash is fabricated for these rows.
    /// </summary>
    public string CanonicalizeForExport()
        => Canonical ?? AuditChain.CanonicalDocument(Id, Seq, new AuditEntry
        {
            ActorType = ActorType,
            ActorId = ActorId,
            Application = Application,
            Action = Action,
            EntityType = EntityType,
            EntityId = EntityId,
            DetailsJson = DetailsJson,
            OccurredAt = OccurredAt,
        });
}

/// <summary>
/// Reads of the trail that the export and the verification share. Both work in
/// sequence order inside one monthly partition, because that is the order the
/// chain was built in: appends serialize under the partition advisory lock, so
/// sequence order is commit order and a sequence range is a contiguous chain
/// segment.
/// </summary>
internal sealed class AuditTrailReader(AuditDbContext db)
{
    private const string SelectRowsSql = """
        SELECT id, seq, occurred_at, actor_type, actor_id, application, action,
               entity_type, entity_id, details::text, canonical, prev_hash, hash
        FROM audit.audit_event
        WHERE occurred_at >= @fromInclusive AND occurred_at < @toExclusive
          AND seq > @afterSeq AND seq <= @throughSeq
        ORDER BY seq
        LIMIT @maxRows
        """;

    private const string MaxSeqOfWindowSql = """
        SELECT COALESCE(MAX(seq), 0)
        FROM audit.audit_event
        WHERE occurred_at >= @fromInclusive AND occurred_at < @toExclusive
        """;

    /// <summary>Highest sequence of the partition; zero when the partition holds nothing.</summary>
    internal Task<long> MaxSeqAsync(
        MonthlyPartitionWindow window,
        CancellationToken cancellationToken)
        => MaxSeqAsync(window.FromInclusive, window.ToExclusive, cancellationToken);

    /// <summary>
    /// Highest sequence among the rows of one day. It is the high-water mark
    /// of the daily export: the slice carries every row up to it, which keeps
    /// the exported segment contiguous even when a late arrival lands with an
    /// older occurrence instant than a row already committed.
    /// </summary>
    internal Task<long> MaxSeqOfDayAsync(DateOnly day, CancellationToken cancellationToken)
        => MaxSeqAsync(day, day.AddDays(1), cancellationToken);

    /// <summary>Rows of the partition in the sequence range, in chain order.</summary>
    internal async Task<IReadOnlyList<AuditTrailRow>> ReadRowsAsync(
        MonthlyPartitionWindow window,
        long afterSeq,
        long throughSeq,
        int maxRows,
        CancellationToken cancellationToken)
    {
        DbConnection connection = await OpenAsync(cancellationToken);
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = SelectRowsSql;
        AddParameter(command, "fromInclusive", ToInstant(window.FromInclusive));
        AddParameter(command, "toExclusive", ToInstant(window.ToExclusive));
        AddParameter(command, "afterSeq", afterSeq);
        AddParameter(command, "throughSeq", throughSeq);
        AddParameter(command, "maxRows", maxRows);

        var rows = new List<AuditTrailRow>();
        await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new AuditTrailRow(
                reader.GetGuid(0),
                reader.GetInt64(1),
                reader.GetFieldValue<DateTimeOffset>(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7),
                reader.GetString(8),
                reader.GetString(9),
                reader.IsDBNull(10) ? null : reader.GetString(10),
                reader.IsDBNull(11) ? null : reader.GetFieldValue<byte[]>(11),
                reader.IsDBNull(12) ? null : reader.GetFieldValue<byte[]>(12)));
        }

        return rows;
    }

    private async Task<long> MaxSeqAsync(
        DateOnly fromInclusive,
        DateOnly toExclusive,
        CancellationToken cancellationToken)
    {
        DbConnection connection = await OpenAsync(cancellationToken);
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = MaxSeqOfWindowSql;
        AddParameter(command, "fromInclusive", ToInstant(fromInclusive));
        AddParameter(command, "toExclusive", ToInstant(toExclusive));
        return (long)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private async Task<DbConnection> OpenAsync(CancellationToken cancellationToken)
    {
        await db.Database.OpenConnectionAsync(cancellationToken);
        return db.Database.GetDbConnection();
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static DateTimeOffset ToInstant(DateOnly day)
        => new(day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
}
