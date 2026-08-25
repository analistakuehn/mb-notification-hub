using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using NotificationHub.Api.Modules.Audit.Domain;
using NotificationHub.Api.Modules.Audit.Infrastructure.Persistence;
using NotificationHub.Api.Modules.Audit.Infrastructure.Verification;
using NotificationHub.Api.Modules.Audit.Integration.V1;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.Audit.Infrastructure.Worm;

/// <summary>
/// The published archive contract over this module's write-once store. It is
/// the same posture the trail export takes: address the object deterministically,
/// ask the store what is already there, and let a matching digest turn a rerun
/// into nothing at all.
/// </summary>
/// <remarks>
/// Nothing here interprets the bytes. The composer owns what the evidence
/// says; this module owns that it cannot be changed afterwards, that it is
/// retained, and that writing it leaves a row in the trail an auditor can find
/// without trusting a log pipeline.
/// </remarks>
internal sealed class WormEvidenceArchive(
    IWormObjectStore store,
    AuditDbContext db,
    IAuditTrail trail,
    IOptions<WormExportOptions> options,
    TimeProvider timeProvider,
    ILogger<WormEvidenceArchive> logger) : IEvidenceArchive
{
    public async Task<Result<ArchivedEvidence>> ArchiveAsync(
        string key,
        ReadOnlyMemory<byte> content,
        string contentType,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        if (!AuditExportKeys.IsSafeRelativeKey(key))
        {
            return Result.ValidationError<ArchivedEvidence>(
                $"A chave de evidência '{key}' não é relativa e segura; use segmentos de letras, dígitos, ponto, hífen e sublinhado.");
        }

        var objectKey = AuditExportKeys.EvidenceObject(options.Value.KeyPrefix, key);
        var bytes = content.ToArray();
        var digest = AuditDigest.Hex(bytes);

        WormObjectHead? existing = await store.HeadAsync(objectKey, cancellationToken);
        if (existing is not null)
        {
            if (!string.Equals(existing.Sha256Hex, digest, StringComparison.Ordinal))
            {
                // Never an overwrite and never a retry: the destination
                // forbids the first, and the second would only hide that what
                // the sources say today no longer matches what was archived.
                logger.EvidenceDigestDiverged(objectKey);
                return Result.IntegrationFailure<ArchivedEvidence>(
                    $"A evidência em '{objectKey}' já existe com digest diferente do recalculado; nada foi sobrescrito.");
            }

            logger.EvidenceAlreadyArchived(objectKey);
            return Result.Success(new ArchivedEvidence
            {
                Key = objectKey,
                Sha256Hex = digest,
                Length = existing.Length,
                AlreadyPresent = true,
            });
        }

        await store.PutAsync(objectKey, bytes, contentType, cancellationToken);
        await RecordAsync(objectKey, bytes.Length, digest, cancellationToken);
        logger.EvidenceArchived(objectKey, bytes.Length, digest);
        return Result.Success(new ArchivedEvidence
        {
            Key = objectKey,
            Sha256Hex = digest,
            Length = bytes.Length,
            AlreadyPresent = false,
        });
    }

    /// <summary>
    /// Writing evidence is a governed effect of this module, so it lands in
    /// the trail like any other. The row is written only when this round wrote
    /// the object: a rerun that found the bytes already there changed nothing
    /// and has nothing to record.
    /// </summary>
    private async Task RecordAsync(
        string objectKey,
        long length,
        string digest,
        CancellationToken cancellationToken)
    {
        await using IDbContextTransaction transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await trail.AppendAsync(
            transaction.GetDbTransaction(),
            new AuditEntry
            {
                ActorType = AuditActorTypes.System,
                ActorId = AuditMaintenanceJournal.ActorId,
                Action = AuditActions.EvidenceArchived,
                EntityType = AuditEntityTypes.EvidenceObject,
                EntityId = objectKey,
                DetailsJson = AuditMaintenanceJournal.Details(
                    [("objectKey", objectKey), ("length", length), ("sha256", digest)]),
                OccurredAt = timeProvider.GetUtcNow(),
            },
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}
