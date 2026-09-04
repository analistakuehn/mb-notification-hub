using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using NotificationHub.Api.Modules.AttachmentManagement.Domain;
using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Persistence;

namespace NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Validation;

internal enum AttachmentValidationStatus
{
    /// <summary>An explicit approval was applied, and a release row records it.</summary>
    Released,

    /// <summary>The attachment is refused for good.</summary>
    Rejected,

    /// <summary>
    /// The verdict did not conclude and the attachment is waiting on its
    /// deadline. It is not released.
    /// </summary>
    Inconclusive,

    /// <summary>Nothing was uploaded, so there is nothing to decide about.</summary>
    NotReceived,

    /// <summary>
    /// A verdict already settled this attachment. Nothing was asked of the
    /// policy and nothing was written, so repeating a validation neither
    /// reopens a refusal nor moves the clock of a release.
    /// </summary>
    AlreadyDecided,

    /// <summary>
    /// The module cannot name exactly one generation for the attachment, so it
    /// cannot say which bytes a verdict would be about. Nothing is written.
    /// </summary>
    IdentityUnavailable,

    /// <summary>
    /// The policy threw, answered nothing, or answered something the durable
    /// state cannot hold. Nothing is written, and the attachment stays exactly
    /// as unreleased as it was.
    /// </summary>
    PolicyUnavailable,

    /// <summary>No attachment carries the reference.</summary>
    UnknownAttachment,
}

/// <summary>
/// What one validation did. The detail is durable state and the public reason
/// is the only thing a producer is told: every content refusal, whatever the
/// detail says, leaves under one reason, so the published vocabulary does not
/// grow a word every time a check is added.
/// </summary>
internal sealed record AttachmentValidationOutcome(
    AttachmentValidationStatus Status,
    string? Detail,
    string? PublicReason);

/// <summary>
/// The state machine of validation and release. It closes by default: the
/// released state is reachable from one place, an explicit approval, and every
/// other path out of this operation leaves the attachment unreleased. A policy
/// that throws, one that answers nothing, one that cannot conclude, and one
/// that is not there at all are four different outcomes here and the same
/// outcome for the recipient.
/// <para>
/// The policy is evaluated once, here. It is not evaluated when the bytes
/// arrive and it is not evaluated again before an attempt goes out: a check
/// that ran again later would let a change to the admitted list reach back and
/// invalidate attachments already accepted, which turns a reversible value into
/// a breaking one. What a later check reads is the release, its instant and its
/// deadline.
/// </para>
/// <para>
/// What this type cannot do, stated because the gate is a security gate. It
/// decides on the verdict it is handed, and the verdict that ships is built
/// from a byte prefix and a list. Nothing here opens the content or looks for
/// malicious code, so an approval means the type agreed and an operator
/// admitted it, and never that the bytes are safe.
/// </para>
/// </summary>
internal sealed class AttachmentValidation(
    AttachmentManagementDbContext dbContext,
    IAttachmentContentPolicy policy,
    IAttachmentSaveOperation saveOperation,
    IOptions<AttachmentValidationOptions> options,
    TimeProvider timeProvider,
    ILogger<AttachmentValidation> logger)
{
    internal async Task<AttachmentValidationOutcome> ValidateAsync(
        AttachmentReference reference,
        CancellationToken cancellationToken)
    {
        AttachmentValidationOptions settings = options.Value;

        // The row is held for the rest of the transaction, so two validations
        // of the same attachment cannot both read a state that is about to
        // stop being true.
        await using IDbContextTransaction transaction =
            await dbContext.Database.BeginTransactionAsync(cancellationToken);
        if (await AttachmentRowLock.AcquireAsync(dbContext, reference, cancellationToken)
            is not { } attachmentId)
        {
            return Outcome(AttachmentValidationStatus.UnknownAttachment);
        }

        Attachment attachment = await dbContext.Attachments
            .SingleAsync(candidate => candidate.Id == attachmentId, cancellationToken);
        DateTimeOffset now = timeProvider.GetUtcNow();

        // The deadline is read before a verdict is asked for, and this order is
        // what makes the deadline mean anything: past it no answer can release
        // the attachment, so asking for one would only be a way back in.
        if (attachment.InconclusiveWindowHasElapsed(now))
        {
            // The wait ends against the attachment without anyone being
            // consulted. A verdict that arrived after the deadline would be a
            // verdict that made the deadline optional.
            return await CloseAsync(attachment, now, transaction, cancellationToken);
        }

        if (attachment.VerdictRefusal() is { } admissibility)
        {
            return Outcome(Refused(admissibility));
        }

        // One generation, or no verdict. Two would leave the release unable to
        // say which bytes it was about, and zero means the identity record has
        // nothing under a reference whose state says the bytes arrived.
        List<AttachmentObjectGeneration> generations = await dbContext.ObjectGenerations
            .AsNoTracking()
            .Where(generation => generation.AttachmentId == attachmentId)
            .ToListAsync(cancellationToken);
        if (generations is not [AttachmentObjectGeneration generation])
        {
            logger.AttachmentIdentityUnavailable(reference.Value, generations.Count);
            return Outcome(AttachmentValidationStatus.IdentityUnavailable);
        }

        AttachmentPolicyVerdict? verdict = await AskAsync(
            reference.Value,
            attachment.ContentType,
            generation,
            cancellationToken);
        if (verdict is null)
        {
            return Outcome(AttachmentValidationStatus.PolicyUnavailable);
        }

        // An approval says everything by being an approval; the other two say
        // nothing without a detail the durable state can hold. A detail that
        // does not fit is treated as no answer, because writing it would either
        // overflow the column or record a refusal nobody can read back.
        if (verdict.Decision != AttachmentPolicyDecision.Approved
            && !Attachment.IsUsableDetail(verdict.Detail))
        {
            logger.AttachmentPolicyDetailUnusable(reference.Value, verdict.Detail?.Length ?? 0);
            return Outcome(AttachmentValidationStatus.PolicyUnavailable);
        }

        AttachmentValidationTransition transition = verdict.Decision switch
        {
            AttachmentPolicyDecision.Approved => attachment.Release(),
            AttachmentPolicyDecision.Refused => attachment.Reject(verdict.Detail, now),
            _ => attachment.HoldInconclusive(verdict.Detail, now, settings.InconclusiveWindow),
        };

        // Unreachable today: the state was read under the row lock a few lines
        // above and nothing since then reloads it. It is not presented as a
        // proven branch and it has no runtime falsifier. It stays because the
        // day this operation rereads the state is the day a verdict could be
        // applied to a state that had stopped accepting it, and the day a
        // release row would be written for an attachment nothing released.
        if (transition != AttachmentValidationTransition.Applied)
        {
            return Outcome(Refused(transition));
        }

        if (verdict.Decision == AttachmentPolicyDecision.Approved)
        {
            // The release is a row of its own, born complete, naming the
            // generation it released. The state and the row become durable in
            // the same transaction, because a released attachment without the
            // row would be a release nothing can date, and a row without the
            // state would be a release nothing points at.
            dbContext.Releases.Add(AttachmentRelease.Grant(
                attachment.Id,
                generation.Id,
                now,
                settings.ReleaseValidity));
        }

        await saveOperation.SaveChangesAsync(dbContext, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Report(attachment, verdict, reference.Value);
    }

    /// <summary>
    /// Ends an attachment whose tolerance ran out, without asking anyone.
    /// </summary>
    private async Task<AttachmentValidationOutcome> CloseAsync(
        Attachment attachment,
        DateTimeOffset now,
        IDbContextTransaction transaction,
        CancellationToken cancellationToken)
    {
        const string detail = AttachmentValidationDetails.InconclusiveWindowElapsed;

        // The window only elapses in the waiting state, and that is a state a
        // verdict may act on, so this transition always applies.
        _ = attachment.Reject(detail, now);
        await saveOperation.SaveChangesAsync(dbContext, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        logger.AttachmentRejected(attachment.Reference.Value, detail);
        return Refusal(detail);
    }

    /// <summary>
    /// Asks the policy, and answers nothing when it did not decide. A policy
    /// that throws and a policy that hands back no verdict are the same event
    /// for the caller and two different lines on the record.
    /// </summary>
    private async Task<AttachmentPolicyVerdict?> AskAsync(
        string reference,
        string declaredContentType,
        AttachmentObjectGeneration generation,
        CancellationToken cancellationToken)
    {
        try
        {
            AttachmentPolicyVerdict? verdict = await policy.EvaluateAsync(
                new AttachmentContentSubject(
                    declaredContentType,
                    generation.DetectedContentType,
                    generation.LengthBytes),
                cancellationToken);
            if (verdict is null)
            {
                logger.AttachmentPolicyAnsweredNothing(reference);
            }

            return verdict;
        }
        catch (Exception exception) when (IsPolicyFailure(exception, cancellationToken))
        {
            // A policy that throws is a policy that did not decide, and the one
            // thing that must not happen next is a release. It is caught here
            // so the caller reads an outcome instead of a stack, and so the
            // incident is on the record either way.
            logger.AttachmentPolicyFailed(exception, reference);
            return null;
        }
    }

    private AttachmentValidationOutcome Report(
        Attachment attachment,
        AttachmentPolicyVerdict verdict,
        string reference)
    {
        if (verdict.Decision == AttachmentPolicyDecision.Approved)
        {
            logger.AttachmentReleased(reference);
            return Outcome(AttachmentValidationStatus.Released);
        }

        if (verdict.Decision == AttachmentPolicyDecision.Refused)
        {
            logger.AttachmentRejected(reference, verdict.Detail);
            return Refusal(verdict.Detail);
        }

        logger.AttachmentVerdictOpen(reference, verdict.Detail, attachment.InconclusiveUntil);
        return new AttachmentValidationOutcome(
            AttachmentValidationStatus.Inconclusive,
            verdict.Detail,
            PublicReason: null);
    }

    private static AttachmentValidationStatus Refused(AttachmentValidationTransition transition)
        => transition == AttachmentValidationTransition.NotReceived
            ? AttachmentValidationStatus.NotReceived
            : AttachmentValidationStatus.AlreadyDecided;

    private static AttachmentValidationOutcome Outcome(AttachmentValidationStatus status)
        => new(status, Detail: null, PublicReason: null);

    /// <summary>
    /// One public reason for the whole family, with the fine detail kept where
    /// only the authorized query reads it.
    /// </summary>
    private static AttachmentValidationOutcome Refusal(string detail)
        => new(AttachmentValidationStatus.Rejected, detail, ErrorCodes.ContentRefused);

    /// <summary>
    /// Everything a policy can raise is a policy that did not decide, except a
    /// cancellation the caller asked for: that one is the caller's own decision
    /// and stays unhandled, so the request that asked for it hears it instead
    /// of reading an unavailable policy.
    /// </summary>
    private static bool IsPolicyFailure(Exception exception, CancellationToken cancellationToken)
        => exception is not OperationCanceledException
            || !cancellationToken.IsCancellationRequested;
}
