using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Persistence;
using NotificationHub.Api.Modules.AttachmentManagement.Integration.V1;

namespace NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Storage;

/// <summary>
/// Settles the witness of one submission against this module's own record of
/// the generations it names.
/// <para>
/// The comparison happens here because the released side of it is here and
/// goes nowhere else. The caller hands over the handle it was given and the
/// measurement it took while writing the bytes out; this resolves the handle,
/// reads the digest and the length recorded when that generation was captured,
/// and answers with a verdict. Nothing about the recorded side crosses back,
/// so the proof of the bytes stays on the row while the composition that
/// describes it stays frozen outside the module.
/// </para>
/// <para>
/// The two sides come from two passes over two readings of the object, taken
/// at different times by different code. The recorded side was measured while
/// the upload was captured and is the value the release was granted over; the
/// submitted side was measured at send time, over what the custody handed back
/// on that call. That is what makes the comparison capable of failing: a
/// witness that recomputed the released digest here, from this same record,
/// would agree with itself for any bytes at all and would certify a message
/// nobody looked at.
/// </para>
/// <para>
/// It reads no state of the attachment and no release. Whether the set was
/// allowed to leave was settled before the call by the check that owns that
/// question, and asking it again after the fact would be a second authority
/// answering later than the one that owns it. What this owns is the identity
/// of the bytes that did leave.
/// </para>
/// <para>
/// The context is created for the reading and disposed with it, for the same
/// reason the reading that hands the content over does it: this serves a
/// caller composed beside a provider adapter, which is a singleton, and a
/// scoped context would pin the first scope that ever resolved it.
/// </para>
/// </summary>
internal sealed class RecordedAttachmentSubmissionWitness(
    IDbContextFactory<AttachmentManagementDbContext> contextFactory,
    ILogger<RecordedAttachmentSubmissionWitness> logger) : IAttachmentSubmissionWitness
{
    public async Task<AttachmentSubmissionVerdict> SettleAsync(
        IReadOnlyList<SubmittedAttachmentBytes> submitted,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(submitted);
        if (submitted.Count == 0)
        {
            throw new ArgumentException(
                "A submission witness covers at least one attachment.",
                nameof(submitted));
        }

        if (submitted.Any(member => member is null
            || string.IsNullOrWhiteSpace(member.ContentIdentity)
            || member.Length < 0))
        {
            throw new ArgumentException(
                "Every submitted attachment names a content identity and a length "
                    + "that is not negative.",
                nameof(submitted));
        }

        var generations = new Guid[submitted.Count];
        for (var index = 0; index < submitted.Count; index++)
        {
            // Text this module never minted names no generation, so there is
            // nothing to compare against and nothing is established. The value
            // itself never reaches the line: a handle is producer-adjacent
            // data and an operational line is where it must not start
            // appearing.
            if (AttachmentContentIdentity.GenerationOf(submitted[index].ContentIdentity)
                is not { } generationId)
            {
                logger.SubmittedHandleNotMinted();
                return AttachmentSubmissionVerdict.Unavailable;
            }

            generations[index] = generationId;
        }

        Dictionary<Guid, AttachmentContentProof> recorded;
        try
        {
            recorded = await RecordedProofsAsync(generations, cancellationToken);
        }
        catch (DbException exception) when (!cancellationToken.IsCancellationRequested)
        {
            // The record could not be read, so nothing about the submitted
            // bytes could be established. Answering that instead of letting
            // the failure travel keeps the decision with the caller, which has
            // already made a call it cannot take back and must not lose the
            // provider verdict over a reading that failed afterwards.
            logger.SubmittedWitnessUnavailable(exception);
            return AttachmentSubmissionVerdict.Unavailable;
        }

        var divergent = 0;
        for (var index = 0; index < submitted.Count; index++)
        {
            Guid generationId = generations[index];

            // A generation that is no longer recorded leaves the comparison
            // with one side missing. It is the absence of a statement about
            // the whole submission and not a divergence of this member,
            // because a member nobody can compare has not been shown to be
            // wrong.
            if (!recorded.TryGetValue(generationId, out AttachmentContentProof? proof))
            {
                logger.SubmittedGenerationGone(generationId);
                return AttachmentSubmissionVerdict.Unavailable;
            }

            SubmittedAttachmentBytes member = submitted[index];
            if (proof.Matches(AttachmentContentProof.Sha256Of(member.Digest, member.Length)))
            {
                continue;
            }

            divergent++;
            logger.SubmittedMemberDiverged(generationId);
        }

        if (divergent > 0)
        {
            logger.SubmittedBytesDiverged(divergent, submitted.Count);
            return AttachmentSubmissionVerdict.Divergent;
        }

        logger.SubmittedBytesMatched(submitted.Count);
        return AttachmentSubmissionVerdict.Matched;
    }

    /// <summary>
    /// What was recorded for each named generation, projected instead of
    /// materialized: the row carries the coordinates too, and they have no
    /// business being in memory on the path that just reached a provider.
    /// </summary>
    private async Task<Dictionary<Guid, AttachmentContentProof>> RecordedProofsAsync(
        Guid[] generations,
        CancellationToken cancellationToken)
    {
        await using AttachmentManagementDbContext dbContext =
            await contextFactory.CreateDbContextAsync(cancellationToken);
        List<StoredProof> rows = await dbContext.ObjectGenerations
            .AsNoTracking()
            .Where(row => generations.Contains(row.Id))
            .Select(row => new StoredProof(row.Id, row.Algorithm, row.Digest, row.LengthBytes))
            .ToListAsync(cancellationToken);

        return rows.ToDictionary(
            row => row.Id,
            row => AttachmentContentProof.Recorded(row.Algorithm, row.Digest, row.LengthBytes));
    }

    private sealed record StoredProof(Guid Id, string Algorithm, byte[] Digest, long LengthBytes);
}
