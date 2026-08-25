using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NotificationHub.Api.Modules.Audit.Infrastructure.Persistence;
using NotificationHub.Api.Modules.Audit.Integration.V1;

namespace NotificationHub.Api.Modules.Audit.Infrastructure.Evidence;

/// <summary>
/// The window read of the trail behind the period contract. Counts are grouped
/// by the database over the queryable columns, which is what those columns are
/// for; every individual event that leaves this reader is rebuilt from the
/// canonical text the hash covers, so a change quoted in a report is the text
/// the chain vouches for and not a column beside it.
/// </summary>
internal sealed class AuditPeriodEvidenceReader(AuditDbContext db) : IAuditPeriodEvidence
{
    /// <summary>
    /// Longest window this read accepts. The trail is partitioned by month and
    /// every index is local to a partition, so the ceiling is what keeps a
    /// mistyped window from turning a report into a full-history scan.
    /// </summary>
    internal static readonly TimeSpan MaxWindow = TimeSpan.FromDays(366);

    /// <summary>
    /// Bound on the governed changes one window may hand over. Publications
    /// and switch transitions are counted in the dozens per month, so the
    /// bound sits far above the shape of the data and exists only so a defect
    /// upstream cannot turn a report into an unbounded materialization.
    /// </summary>
    private const int MaxGovernedChanges = 10_000;

    // The reason is named in the filter and in the grouping on purpose: the
    // extraction is what the group is keyed by, and stating it in the filter
    // is what keeps rows without a reason out of a bucket that would
    // otherwise be named null.
    private const string ReasonCountsSql = """
        SELECT action, details->>'reason' AS reason, COUNT(*)::bigint AS occurrences
        FROM audit.audit_event
        WHERE occurred_at >= @fromInclusive
          AND occurred_at < @toExclusive
          AND details->>'reason' IS NOT NULL
        GROUP BY action, details->>'reason'
        ORDER BY action, reason
        """;

    public async Task<AuditPeriodEvidence> SummarizeAsync(
        DateTimeOffset fromInclusive,
        DateTimeOffset toExclusive,
        CancellationToken cancellationToken)
    {
        if (toExclusive <= fromInclusive)
        {
            throw new ArgumentException(
                "A janela da evidência periódica precisa terminar depois de começar.", nameof(toExclusive));
        }

        if (toExclusive - fromInclusive > MaxWindow)
        {
            throw new ArgumentException(
                $"A janela da evidência periódica precisa ser de no máximo {MaxWindow.TotalDays:F0} dias.",
                nameof(toExclusive));
        }

        List<ActionCountRow> actionRows = await db.AuditEvents
            .AsNoTracking()
            .Where(candidate => candidate.OccurredAt >= fromInclusive && candidate.OccurredAt < toExclusive)
            .GroupBy(candidate => candidate.Action)
            .Select(group => new ActionCountRow(group.Key, group.LongCount()))
            .ToListAsync(cancellationToken);

        var unchainedRows = await db.AuditEvents
            .AsNoTracking()
            .Where(candidate => candidate.OccurredAt >= fromInclusive
                && candidate.OccurredAt < toExclusive
                && candidate.Hash == null)
            .LongCountAsync(cancellationToken);

        string[] governedActions = [.. AuditGovernedChangeActions.All];
        List<ChangeRow> changeRows = await db.AuditEvents
            .AsNoTracking()
            .Where(candidate => candidate.OccurredAt >= fromInclusive
                && candidate.OccurredAt < toExclusive
                && governedActions.Contains(candidate.Action)
                && candidate.Canonical != null
                && candidate.Hash != null)
            .OrderBy(candidate => candidate.Seq)
            .Take(MaxGovernedChanges)
            .Select(candidate => new ChangeRow(candidate.Canonical!, candidate.Hash!))
            .ToListAsync(cancellationToken);

        List<ApprovalRecord> approvals = await db.Approvals
            .AsNoTracking()
            .Where(candidate => candidate.ApprovedAt >= fromInclusive && candidate.ApprovedAt < toExclusive)
            .OrderBy(candidate => candidate.ApprovedAt)
            .ThenBy(candidate => candidate.Id)
            .Select(candidate => new ApprovalRecord
            {
                SubjectType = candidate.SubjectType,
                SubjectId = candidate.SubjectId,
                SubjectVersion = candidate.SubjectVersion,
                ContentHash = candidate.ContentHash,
                Role = candidate.Role,
                ApproverOid = candidate.ApproverOid,
                ApprovedAt = candidate.ApprovedAt,
            })
            .ToListAsync(cancellationToken);

        List<VerificationRow> verificationRows = await db.AuditEvents
            .AsNoTracking()
            .Where(candidate => candidate.OccurredAt >= fromInclusive
                && candidate.OccurredAt < toExclusive
                && (candidate.Action == AuditActions.AuditChainVerified
                    || candidate.Action == AuditActions.AuditChainVerificationFailed))
            .GroupBy(candidate => new { candidate.EntityId, candidate.Action })
            .Select(group => new VerificationRow(
                group.Key.EntityId,
                group.Key.Action,
                group.LongCount(),
                group.Max(candidate => candidate.OccurredAt)))
            .ToListAsync(cancellationToken);

        IReadOnlyList<AuditActionReasonCount> reasonCounts =
            await ReadReasonCountsAsync(fromInclusive, toExclusive, cancellationToken);

        return new AuditPeriodEvidence
        {
            FromInclusive = fromInclusive,
            ToExclusive = toExclusive,
            ActionCounts = [.. actionRows
                .OrderBy(row => row.Action, StringComparer.Ordinal)
                .Select(row => new AuditActionCount { Action = row.Action, Count = row.Count })],
            ReasonCounts = reasonCounts,
            GovernedChanges = [.. changeRows.Select(ToChange)],
            Approvals = approvals,
            ChainVerifications = ToVerifications(verificationRows),
            UnchainedRows = unchainedRows,
        };
    }

    /// <summary>
    /// The reason breakdown, grouped by the database over the details column.
    /// The column is the queryable surface of the trail and never the payload
    /// of proof, which is exactly what a count is: what a single refusal says
    /// stays a matter for the canonical text.
    /// </summary>
    private async Task<IReadOnlyList<AuditActionReasonCount>> ReadReasonCountsAsync(
        DateTimeOffset fromInclusive,
        DateTimeOffset toExclusive,
        CancellationToken cancellationToken)
    {
        await db.Database.OpenConnectionAsync(cancellationToken);
        DbConnection connection = db.Database.GetDbConnection();
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = ReasonCountsSql;
        AddParameter(command, "fromInclusive", fromInclusive);
        AddParameter(command, "toExclusive", toExclusive);

        var counts = new List<AuditActionReasonCount>();
        await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            counts.Add(new AuditActionReasonCount
            {
                Action = reader.GetString(0),
                Reason = reader.GetString(1),
                Count = reader.GetInt64(2),
            });
        }

        return counts;
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    /// <summary>
    /// One governed change, entirely out of the canonical text. A row whose
    /// canonical text does not parse is corruption of the evidence itself, not
    /// a shape this reader may smooth over.
    /// </summary>
    private static AuditGovernedChange ToChange(ChangeRow row)
    {
        using var document = JsonDocument.Parse(row.Canonical);
        JsonElement root = document.RootElement;
        return new AuditGovernedChange
        {
            Seq = root.GetProperty("seq").GetInt64(),
            Action = Text(root, "action")!,
            EntityType = Text(root, "entityType")!,
            EntityId = Text(root, "entityId")!,
            ActorType = Text(root, "actorType")!,
            ActorId = Text(root, "actorId")!,
            Application = Text(root, "application"),
            OccurredAt = root.GetProperty("occurredAt").GetDateTimeOffset(),
            Hash = Convert.ToHexStringLower(row.Hash),
        };
    }

    private static IReadOnlyList<AuditChainVerificationOutcome> ToVerifications(IReadOnlyList<VerificationRow> rows)
        => [.. rows
            .GroupBy(row => row.Partition, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new AuditChainVerificationOutcome
            {
                Partition = group.Key,
                IntactRounds = Rounds(group, AuditActions.AuditChainVerified),
                FailedRounds = Rounds(group, AuditActions.AuditChainVerificationFailed),
                LastIntactAt = Last(group, AuditActions.AuditChainVerified),
                LastFailureAt = Last(group, AuditActions.AuditChainVerificationFailed),
            })];

    private static long Rounds(IEnumerable<VerificationRow> rows, string action)
        => rows.Where(row => string.Equals(row.Action, action, StringComparison.Ordinal)).Sum(row => row.Count);

    private static DateTimeOffset? Last(IEnumerable<VerificationRow> rows, string action)
        => rows
            .Where(row => string.Equals(row.Action, action, StringComparison.Ordinal))
            .Select(row => (DateTimeOffset?)row.LastOccurredAt)
            .Max();

    private static string? Text(JsonElement root, string name)
        => root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private sealed record ActionCountRow(string Action, long Count);

    private sealed record ChangeRow(string Canonical, byte[] Hash);

    private sealed record VerificationRow(string Partition, string Action, long Count, DateTimeOffset LastOccurredAt);
}
