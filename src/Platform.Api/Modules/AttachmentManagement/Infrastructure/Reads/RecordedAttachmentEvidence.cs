using Microsoft.EntityFrameworkCore;
using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Persistence;
using NotificationHub.Api.Modules.AttachmentManagement.Integration.V1;

namespace NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Reads;

/// <summary>
/// The evidence read of this module, resolving opaque content handles against
/// the durable record they were minted from.
/// <para>
/// Every projection here names its columns one by one, and the store, the key
/// and the generation of the provider are named in none of them. A coordinate
/// left out of a projection cannot be served by accident later, and that is the
/// whole guard: this type answers an authorized reader with proof of which
/// bytes were accepted and with no way of reaching them.
/// </para>
/// <para>
/// It reads with tracking off and writes nothing. The aggregate carries a row
/// version, so a write from here would invalidate whatever had loaded the row
/// before this read, for an answer that changes nothing it describes.
/// </para>
/// </summary>
internal sealed class RecordedAttachmentEvidence(AttachmentManagementDbContext dbContext)
    : IAttachmentEvidence
{
    public async Task<IReadOnlyDictionary<string, AttachmentEvidence>> DescribeAcceptedContentAsync(
        IReadOnlyCollection<string> contentIdentities,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(contentIdentities);

        // The handle is the key the caller asked with, and it is what the
        // answer comes back under. Text this module never minted resolves to
        // no generation and drops out here, before any statement is built.
        Dictionary<Guid, string> asked = [];
        foreach (var contentIdentity in contentIdentities)
        {
            if (AttachmentContentIdentity.GenerationOf(contentIdentity) is { } generationId)
            {
                asked.TryAdd(generationId, contentIdentity);
            }
        }

        if (asked.Count == 0)
        {
            return new Dictionary<string, AttachmentEvidence>(StringComparer.Ordinal);
        }

        Guid[] generationIds = [.. asked.Keys];
        List<GenerationRow> generations = await dbContext.ObjectGenerations
            .AsNoTracking()
            .Where(generation => generationIds.Contains(generation.Id))
            .Join(
                dbContext.Attachments.AsNoTracking(),
                generation => generation.AttachmentId,
                attachment => attachment.Id,
                (generation, attachment) => new GenerationRow(
                    generation.Id,
                    attachment.Reference.Value,
                    attachment.Application,
                    attachment.State,
                    attachment.ValidationDetail,
                    generation.Algorithm,
                    generation.Digest,
                    generation.LengthBytes,
                    generation.DetectedContentType,
                    generation.CapturedAt))
            .ToListAsync(cancellationToken);

        Dictionary<Guid, GrantRow> grants = await ReadGrantsAsync(generationIds, cancellationToken);

        var evidence = new Dictionary<string, AttachmentEvidence>(StringComparer.Ordinal);
        foreach (GenerationRow row in generations)
        {
            grants.TryGetValue(row.GenerationId, out GrantRow? grant);
            evidence[asked[row.GenerationId]] = new AttachmentEvidence
            {
                ContentIdentity = asked[row.GenerationId],
                Reference = row.Reference,
                Application = row.Application,
                State = row.State,
                ValidationDetail = row.ValidationDetail,
                DigestAlgorithm = row.Algorithm,
                Digest = Convert.ToHexStringLower(row.Digest),
                DigestedLengthBytes = row.LengthBytes,
                DetectedContentType = row.DetectedContentType,
                CapturedAt = row.CapturedAt,
                ReleasedAt = grant?.ReleasedAt,
                RevokedAt = grant?.RevokedAt,
                RevocationReason = grant?.RevocationReason,
            };
        }

        return evidence;
    }

    /// <summary>
    /// The grant over each generation asked about, with the withdrawal of that
    /// exact grant when one exists.
    /// <para>
    /// Keyed on the generation and never on the attachment, because that is the
    /// question an accepted snapshot poses: it froze which bytes were accepted,
    /// and a revalidation writes a second grant over other bytes. Answering
    /// with the latest grant of the attachment would date the approval of
    /// content the notification never carried.
    /// </para>
    /// <para>
    /// The store does the ordering, on the same two columns the release check
    /// and the claim already order by, so a generation released twice yields
    /// one answer everywhere rather than one per reader.
    /// </para>
    /// </summary>
    private async Task<Dictionary<Guid, GrantRow>> ReadGrantsAsync(
        Guid[] generationIds,
        CancellationToken cancellationToken)
    {
        List<ReleaseRow> releases = await dbContext.Releases
            .AsNoTracking()
            .Where(release => generationIds.Contains(release.GenerationId))
            .OrderByDescending(release => release.ReleasedAt)
            .ThenByDescending(release => release.Id)
            .Select(release => new ReleaseRow(
                release.Id, release.GenerationId, release.ReleasedAt))
            .ToListAsync(cancellationToken);

        var granted = new Dictionary<Guid, ReleaseRow>();
        foreach (ReleaseRow release in releases)
        {
            granted.TryAdd(release.GenerationId, release);
        }

        Guid[] releaseIds = [.. granted.Values.Select(release => release.Id)];
        List<RevocationRow> withdrawals = releaseIds.Length == 0
            ? []
            : await dbContext.Revocations
                .AsNoTracking()
                .Where(revocation => releaseIds.Contains(revocation.ReleaseId))
                .Select(revocation => new RevocationRow(
                    revocation.ReleaseId, revocation.RevokedAt, revocation.Reason))
                .ToListAsync(cancellationToken);
        var takenBack = withdrawals.ToDictionary(
            revocation => revocation.ReleaseId);

        var grants = new Dictionary<Guid, GrantRow>();
        foreach ((Guid generationId, ReleaseRow release) in granted)
        {
            takenBack.TryGetValue(release.Id, out RevocationRow? revocation);
            grants[generationId] = new GrantRow(
                release.ReleasedAt, revocation?.RevokedAt, revocation?.Reason);
        }

        return grants;
    }

    private sealed record GenerationRow(
        Guid GenerationId,
        string Reference,
        string Application,
        string State,
        string? ValidationDetail,
        string Algorithm,
        byte[] Digest,
        long LengthBytes,
        string? DetectedContentType,
        DateTimeOffset CapturedAt);

    private sealed record ReleaseRow(Guid Id, Guid GenerationId, DateTimeOffset ReleasedAt);

    private sealed record RevocationRow(
        Guid ReleaseId,
        DateTimeOffset RevokedAt,
        string Reason);

    private sealed record GrantRow(
        DateTimeOffset ReleasedAt,
        DateTimeOffset? RevokedAt,
        string? RevocationReason);
}
