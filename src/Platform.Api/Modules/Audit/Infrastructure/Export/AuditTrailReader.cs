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

        using var document = JsonDocument.Parse(Canonical);
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
/// <remarks>
/// <para>
/// The rows come back through two statements instead of one, chained and
/// pre-chain, merged here in sequence order. The split is what lets each side
/// carry the predicate of the partial index that answers it, which removes the
/// sort the single statement paid for: one query over a partition of millions
/// of wide rows had to order them all, and the ordering carried the canonical
/// text with it. The boundary is not new either, it is the one the export
/// already writes to separate objects, so the reader now states in code the
/// limit of what the chain covers.
/// </para>
/// <para>
/// Each side advances by key over <c>seq</c> in blocks, so no statement ever
/// materializes the whole partition. That makes a long pass interruptible and
/// resumable rather than one transaction that holds a snapshot for minutes. The
/// consequence to keep in mind: blocks take one snapshot each, so a row that
/// commits between two blocks with a sequence value the reader already passed
/// stays out of this pass. That exposure predates the blocks, since a single
/// statement also reads one snapshot, and it is what the stabilization
/// watermark of the verification and the high-water mark of the export exist to
/// bound; a chain segment missing a link fails the replay loudly instead of
/// being exported.
/// </para>
/// </remarks>
internal sealed class AuditTrailReader(AuditDbContext db)
{
    /// <summary>Rows fetched per statement while walking a sequence range.</summary>
    private const int BlockRows = 5_000;

    private const string ChainedRowsSql = """
        SELECT id, seq, occurred_at, actor_type, actor_id, application, action,
               entity_type, entity_id, details::text, canonical, prev_hash, hash
        FROM audit.audit_event
        WHERE occurred_at >= @fromInclusive AND occurred_at < @toExclusive
          AND hash IS NOT NULL
          AND seq > @afterSeq AND seq <= @throughSeq
        ORDER BY seq
        LIMIT @maxRows
        """;

    private const string PreChainRowsSql = """
        SELECT id, seq, occurred_at, actor_type, actor_id, application, action,
               entity_type, entity_id, details::text, canonical, prev_hash, hash
        FROM audit.audit_event
        WHERE occurred_at >= @fromInclusive AND occurred_at < @toExclusive
          AND hash IS NULL
          AND seq > @afterSeq AND seq <= @throughSeq
        ORDER BY seq
        LIMIT @maxRows
        """;

    // Two predicate-carrying halves instead of one aggregate over the whole
    // partition, so each half is answered by the partial index that covers it.
    // Taking only the chained half would be wrong for a partition that holds
    // nothing but pre-chain rows: its highest sequence is real and the export
    // has to reach it.
    private const string MaxSeqOfWindowSql = """
        SELECT GREATEST(
            COALESCE((
                SELECT MAX(seq) FROM audit.audit_event
                WHERE occurred_at >= @fromInclusive AND occurred_at < @toExclusive
                  AND hash IS NOT NULL), 0),
            COALESCE((
                SELECT MAX(seq) FROM audit.audit_event
                WHERE occurred_at >= @fromInclusive AND occurred_at < @toExclusive
                  AND hash IS NULL), 0))
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
        ArgumentNullException.ThrowIfNull(window);
        var rows = new List<AuditTrailRow>();
        if (maxRows <= 0)
        {
            return rows;
        }

        DbConnection connection = await OpenAsync(cancellationToken);
        var block = Math.Min(BlockRows, maxRows);
        var chained = new SequencePages(connection, ChainedRowsSql, window, afterSeq, throughSeq, block);
        var preChain = new SequencePages(connection, PreChainRowsSql, window, afterSeq, throughSeq, block);

        // Merged here rather than by the database: both halves arrive already
        // ordered by sequence, and merging two ordered streams costs one
        // comparison per row against the sort the split just removed.
        while (rows.Count < maxRows)
        {
            AuditTrailRow? nextChained = await chained.PeekAsync(cancellationToken);
            AuditTrailRow? nextPreChain = await preChain.PeekAsync(cancellationToken);
            if (nextChained is null && nextPreChain is null)
            {
                break;
            }

            var takeChained = nextPreChain is null
                || (nextChained is not null && nextChained.Seq <= nextPreChain.Seq);
            rows.Add(takeChained ? chained.Take() : preChain.Take());
        }

        return rows;
    }

    /// <summary>One block of one half of the range, read by key over the sequence.</summary>
    private static async Task<IReadOnlyList<AuditTrailRow>> PageAsync(
        DbConnection connection,
        string sql,
        MonthlyPartitionWindow window,
        long afterSeq,
        long throughSeq,
        int maxRows,
        CancellationToken cancellationToken)
    {
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = sql;
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

    /// <summary>
    /// One half of the range, walked by key: every block starts after the
    /// highest sequence the previous block returned, so no statement depends on
    /// an offset and none of them grows with the size of the partition.
    /// </summary>
    private sealed class SequencePages
    {
        private readonly DbConnection _connection;
        private readonly string _sql;
        private readonly MonthlyPartitionWindow _window;
        private readonly long _throughSeq;
        private readonly int _blockRows;
        private readonly Queue<AuditTrailRow> _buffered = new();
        private long _cursor;
        private bool _drained;

        internal SequencePages(
            DbConnection connection,
            string sql,
            MonthlyPartitionWindow window,
            long afterSeq,
            long throughSeq,
            int blockRows)
        {
            _connection = connection;
            _sql = sql;
            _window = window;
            _cursor = afterSeq;
            _throughSeq = throughSeq;
            _blockRows = blockRows;
        }

        internal async Task<AuditTrailRow?> PeekAsync(CancellationToken cancellationToken)
        {
            if (_buffered.Count == 0 && !_drained)
            {
                IReadOnlyList<AuditTrailRow> page = await PageAsync(
                    _connection, _sql, _window, _cursor, _throughSeq, _blockRows, cancellationToken);
                foreach (AuditTrailRow row in page)
                {
                    _buffered.Enqueue(row);
                }

                // The cursor moves with what was fetched, not with what was
                // consumed: a short block is the end of this half.
                _drained = page.Count < _blockRows;
                if (page.Count > 0)
                {
                    _cursor = page[^1].Seq;
                }
            }

            return _buffered.Count > 0 ? _buffered.Peek() : null;
        }

        internal AuditTrailRow Take() => _buffered.Dequeue();
    }
}
