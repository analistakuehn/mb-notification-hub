using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NotificationHub.Api.Modules.Audit.Infrastructure.Persistence;
using NotificationHub.Api.Modules.Audit.Integration.V1;

namespace NotificationHub.Api.Modules.Audit.Infrastructure.Evidence;

/// <summary>
/// Rebuilds trail links from the canonical text of each row. The query filters
/// on the scalar columns and the occurrence window, which is what the indexes
/// and the monthly partitions serve, and then every field of the answer is
/// parsed out of the canonical text the hash covers. The details column is
/// never read: jsonb re-serializes on read, so its bytes are outside the chain.
/// </summary>
internal sealed class AuditEvidenceReader(AuditDbContext db) : IAuditEvidence
{
    public Task<AuditSubjectLinks> ReadLinksAsync(
        AuditSubject subject,
        DateTimeOffset fromInclusive,
        DateTimeOffset toExclusive,
        CancellationToken cancellationToken)
        => ReadLinksAsync([subject], fromInclusive, toExclusive, cancellationToken);

    public async Task<AuditSubjectLinks> ReadLinksAsync(
        IReadOnlyCollection<AuditSubject> subjects,
        DateTimeOffset fromInclusive,
        DateTimeOffset toExclusive,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subjects);
        if (subjects.Count == 0)
        {
            return new AuditSubjectLinks { Links = [], UnchainedRows = 0 };
        }

        // Composite keys do not translate to a single IN list, and one subject
        // set is small: the predicate is built from the distinct entity types
        // and ids, then narrowed in memory to the exact pairs asked for. The
        // window and the type filter still do the partition pruning.
        AuditSubject[] wanted = [.. subjects.Distinct()];
        string[] entityTypes = [.. wanted.Select(subject => subject.EntityType).Distinct(StringComparer.Ordinal)];
        string[] entityIds = [.. wanted.Select(subject => subject.EntityId).Distinct(StringComparer.Ordinal)];

        List<TrailRow> rows = await db.AuditEvents
            .AsNoTracking()
            .Where(candidate => candidate.OccurredAt >= fromInclusive
                && candidate.OccurredAt < toExclusive
                && entityTypes.Contains(candidate.EntityType)
                && entityIds.Contains(candidate.EntityId))
            .OrderBy(candidate => candidate.Seq)
            .Select(candidate => new TrailRow(
                candidate.Seq,
                candidate.EntityType,
                candidate.EntityId,
                candidate.Canonical,
                candidate.PrevHash,
                candidate.Hash))
            .ToListAsync(cancellationToken);

        HashSet<AuditSubject> pairs = [.. wanted];
        List<TrailRow> matching = [.. rows.Where(row => pairs.Contains(new AuditSubject(row.EntityType, row.EntityId)))];

        return new AuditSubjectLinks
        {
            Links = [.. matching
                .Where(row => row is { Canonical: not null, PrevHash: not null, Hash: not null })
                .Select(ToLink)],
            UnchainedRows = matching.Count(row => row.Canonical is null),
        };
    }

    public async Task<IReadOnlyList<ApprovalRecord>> ReadApprovalsAsync(
        string subjectType,
        string subjectId,
        int subjectVersion,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subjectType);
        ArgumentException.ThrowIfNullOrWhiteSpace(subjectId);

        return await db.Approvals
            .AsNoTracking()
            .Where(candidate => candidate.SubjectType == subjectType
                && candidate.SubjectId == subjectId
                && candidate.SubjectVersion == subjectVersion)
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
    }

    /// <summary>
    /// The whole link comes from the canonical text. A row whose canonical text
    /// does not parse is corruption of the evidence itself, not a shape this
    /// reader may smooth over.
    /// </summary>
    private static AuditLink ToLink(TrailRow row)
    {
        JsonElement document = Parse(row);
        return new AuditLink
        {
            Seq = document.GetProperty("seq").GetInt64(),
            Hash = Convert.ToHexStringLower(row.Hash!),
            PrevHash = Convert.ToHexStringLower(row.PrevHash!),
            Canonical = row.Canonical!,
            Action = Text(document, "action"),
            ActorType = Text(document, "actorType"),
            ActorId = Text(document, "actorId"),
            Application = document.GetProperty("application") is { ValueKind: JsonValueKind.String } application
                ? application.GetString()
                : null,
            EntityType = Text(document, "entityType"),
            EntityId = Text(document, "entityId"),
            OccurredAt = document.GetProperty("occurredAt").GetDateTimeOffset(),
            Details = document.GetProperty("details"),
        };
    }

    private static JsonElement Parse(TrailRow row)
    {
        try
        {
            using JsonDocument parsed = JsonDocument.Parse(row.Canonical!);
            return parsed.RootElement.Clone();
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                $"O texto canônico do evento de auditoria {row.Seq} não é um documento JSON válido.",
                exception);
        }
    }

    private static string Text(JsonElement document, string member)
        => document.GetProperty(member).GetString()
            ?? throw new InvalidOperationException(
                $"O texto canônico do evento de auditoria não declara '{member}'.");

    private sealed record TrailRow(
        long Seq,
        string EntityType,
        string EntityId,
        string? Canonical,
        byte[]? PrevHash,
        byte[]? Hash);
}
