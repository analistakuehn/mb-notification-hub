using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NotificationHub.Api.Modules.AttachmentManagement.Domain;
using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Persistence;
using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Validation;
using NotificationHub.Api.Modules.AttachmentManagement.Integration.V1;

namespace NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Release;

/// <summary>
/// Reads whether every member of an accepted set still carries a release in
/// force, from this module's own durable record and from nowhere else.
/// <para>
/// It is the other half of the snapshot. The snapshot froze which attachments
/// the notification carries and which content each of them was accepted with,
/// and it froze no permission at all; this is where the permission is read,
/// every time, immediately before the call that cannot be taken back. Four
/// things stop a member here, and the caller is told one word for the four
/// because its next step is the same for all of them: the attachment is not
/// there, its state is no longer released, the release in force names other
/// content than the one that was accepted, or that release is past its
/// validity.
/// </para>
/// <para>
/// The validity is computed on every reading and never taken from the deadline
/// stored with the grant. It counts from the later of the release and the
/// instant the current validity took effect, which is what keeps a shortened
/// validity from expiring, on the deployment itself, every release older than
/// the new duration.
/// </para>
/// <para>
/// It opens no content and calls no provider. Proving that the bytes behind a
/// generation are still those bytes belongs to the path that opens them, and
/// this check is the gate that keeps that far heavier path from being entered
/// at all.
/// </para>
/// </summary>
internal sealed class RecordedAttachmentReleaseCheck(
    AttachmentManagementDbContext dbContext,
    IOptions<AttachmentValidationOptions> options,
    TimeProvider timeProvider,
    ILogger<RecordedAttachmentReleaseCheck> logger) : IAttachmentReleaseCheck
{
    public async Task<AttachmentReleaseVerdict> VerifyAsync(
        AcceptedAttachmentSet accepted,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(accepted);

        try
        {
            Dictionary<string, Guid> released = await ReleasedAttachmentsAsync(
                accepted, cancellationToken);
            Dictionary<Guid, AttachmentRelease> inForce = await ReleasesInForceAsync(
                [.. released.Values], cancellationToken);

            AttachmentValidationOptions settings = options.Value;
            DateTimeOffset now = timeProvider.GetUtcNow();
            var withheld = accepted.Count(
                item => !IsDeliverable(item, released, inForce, settings, now));
            if (withheld == 0)
            {
                return AttachmentReleaseVerdict.Deliverable;
            }

            // How many, and never which. Which member refused and which of the
            // four reasons closed are both reconstructable from the
            // notification row and this module's authorized lifecycle read, and
            // an operational line is exactly where a reference, a name and a
            // media type must not start appearing.
            logger.AcceptedSetWithheld(withheld, accepted.Count);
            return AttachmentReleaseVerdict.Withheld;
        }
        catch (DbException exception) when (!cancellationToken.IsCancellationRequested)
        {
            // The record could not be read, so nothing is known about the set.
            // Answering that, instead of letting the failure travel, is what
            // lets a caller hold an attempt it has already claimed: a throw
            // here would leave that attempt owned by nobody and unsendable
            // forever, which is worse than a send that waits.
            logger.AcceptedSetCheckUnavailable(exception);
            return AttachmentReleaseVerdict.Unavailable;
        }
    }

    /// <summary>
    /// Whether one member is still what it was accepted as. Every branch here
    /// is a refusal, and the last line is the only way through.
    /// </summary>
    private static bool IsDeliverable(
        AcceptedAttachment item,
        Dictionary<string, Guid> released,
        Dictionary<Guid, AttachmentRelease> inForce,
        AttachmentValidationOptions settings,
        DateTimeOffset now)
    {
        // A reference this module no longer answers for, and one whose
        // attachment is no longer released, both leave the set a member short,
        // and a set that is short a member is not the set that was accepted.
        if (!released.TryGetValue(item.Reference, out Guid attachmentId))
        {
            return false;
        }

        // A state that says released with no release naming it is a record
        // that cannot say which bytes were approved.
        if (!inForce.TryGetValue(attachmentId, out AttachmentRelease? release))
        {
            return false;
        }

        // The handle names a generation and the release in force names one too.
        // Divergence is the two naming different generations, and text this
        // module never minted names no generation at all, so both leave through
        // the same comparison.
        if (AttachmentContentIdentity.GenerationOf(item.ContentIdentity) != release.GenerationId)
        {
            return false;
        }

        return release.IsValidAt(now, settings.ReleaseValidity, settings.ValidityEffectiveFrom);
    }

    /// <summary>
    /// The attachments behind the references, keeping only those the module
    /// still calls released. A revoked or rejected attachment drops out here
    /// and never reaches the comparison of content.
    /// </summary>
    private async Task<Dictionary<string, Guid>> ReleasedAttachmentsAsync(
        AcceptedAttachmentSet accepted,
        CancellationToken cancellationToken)
    {
        AttachmentReference[] references =
            [.. accepted.Select(item => AttachmentReference.Trusted(item.Reference))];
        List<AttachmentRow> rows = await dbContext.Attachments
            .AsNoTracking()
            .Where(attachment => references.Contains(attachment.Reference)
                && attachment.State == AttachmentStates.Released)
            .Select(attachment => new AttachmentRow(attachment.Reference.Value, attachment.Id))
            .ToListAsync(cancellationToken);

        return rows.ToDictionary(row => row.Reference, row => row.Id, StringComparer.Ordinal);
    }

    /// <summary>
    /// The release in force for each attachment: the latest one, because an
    /// explicit revalidation writes a second row with an instant of its own,
    /// and it is that second grant which says what may go out now.
    /// <para>
    /// The store does the ordering rather than this process. The claim that
    /// produced the snapshot ordered the same rows there, on the same two
    /// columns, and two orderings of one set are two answers to which
    /// generation is in force.
    /// </para>
    /// </summary>
    private async Task<Dictionary<Guid, AttachmentRelease>> ReleasesInForceAsync(
        Guid[] attachmentIds,
        CancellationToken cancellationToken)
    {
        List<AttachmentRelease> rows = await dbContext.Releases
            .AsNoTracking()
            .Where(release => attachmentIds.Contains(release.AttachmentId))
            .OrderByDescending(release => release.ReleasedAt)
            .ThenByDescending(release => release.Id)
            .ToListAsync(cancellationToken);

        var inForce = new Dictionary<Guid, AttachmentRelease>();
        foreach (AttachmentRelease row in rows)
        {
            inForce.TryAdd(row.AttachmentId, row);
        }

        return inForce;
    }

    private sealed record AttachmentRow(string Reference, Guid Id);
}
