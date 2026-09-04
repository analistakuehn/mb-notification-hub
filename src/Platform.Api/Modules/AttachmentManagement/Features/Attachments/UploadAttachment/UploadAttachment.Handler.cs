using System.Runtime.ExceptionServices;
using Microsoft.EntityFrameworkCore;
using NotificationHub.Api.Modules.AttachmentManagement.Domain;
using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Persistence;
using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Storage;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.AttachmentManagement.Features.Attachments;

internal static partial class UploadAttachment
{
    internal sealed class Handler(
        AttachmentManagementDbContext dbContext,
        IDbContextFactory<AttachmentManagementDbContext> dbContextFactory,
        IAttachmentSaveOperation saveOperation,
        IAttachmentObjectStore objectStore,
        TimeProvider timeProvider,
        ILogger<Handler> logger)
    {
        /// <summary>
        /// How long the removal of a generation this upload will not keep may
        /// take. It runs on its own deadline and not on the caller's token,
        /// because the bytes have to go even when the caller has already left;
        /// not being tied to the caller is not the same as having no limit,
        /// and this endpoint is rate limited, so a request slot held for the
        /// client library's own budget turns into refused legitimate traffic.
        /// </summary>
        private static readonly TimeSpan CompensationBudget = TimeSpan.FromSeconds(10);

        /// <summary>
        /// How long the note that this attachment owes a reclaim may take. It
        /// runs on its own deadline for the same reason the removal above
        /// does, and a shorter one, because it is a single indexed statement
        /// against a database this request has already been talking to.
        /// </summary>
        private static readonly TimeSpan LiabilityBudget = TimeSpan.FromSeconds(5);

        public async Task<Result<Response>> HandleAsync(
            Command command,
            CancellationToken cancellationToken)
        {
            Result<AttachmentReference> reference = AttachmentReference.Create(command.Reference);
            if (reference.IsFailure)
            {
                return Result.NotFound<Response>(ErrorCodes.NotFound);
            }

            Attachment? attachment = await dbContext.Attachments
                .SingleOrDefaultAsync(
                    candidate => candidate.Reference == reference.Value,
                    cancellationToken);
            if (attachment is null)
            {
                return Result.NotFound<Response>(ErrorCodes.NotFound);
            }

            if (attachment.State == AttachmentStates.Received)
            {
                return Result.BusinessRuleViolation<Response>(ErrorCodes.AlreadyReceived);
            }

            if (command.DeclaredSizeBytes is not { } declared
                || declared != attachment.SizeBytes)
            {
                return Result.ValidationError<Response>(ErrorCodes.SizeMismatch);
            }

            // Doubling the traffic to the provider is the price of measuring
            // what stayed instead of what left: one pass writes the bytes and
            // a second pass reads the pinned generation back. No budget, gate
            // or concurrency limit describes that second pass today; the rate
            // limiter grants permits per principal without weighing bytes.
            AttachmentObjectCapture capture = await objectStore.PutAsync(
                new AttachmentObjectRequest(
                    attachment.ContentId,
                    attachment.ContentType,
                    attachment.SizeBytes),
                command.Content,
                cancellationToken);
            if (capture is not
                {
                    Status: AttachmentObjectCaptureStatus.Captured,
                    Locator: { } locator,
                })
            {
                // A write the store took without naming a generation leaves
                // the key held by bytes this module cannot remove, and from
                // here on every retry of this upload meets the same refusal.
                // It is recorded against the row because nothing else will
                // ever discover it.
                //
                // The conditional write is what makes this note safe: it
                // placed these bytes, so no other request can have placed any,
                // and there is no upload of this attachment in flight that
                // could still succeed.
                //
                // The conflict is deliberately not recorded here, and the
                // reason is measured rather than argued. A conflict means
                // somebody else's write holds the key, and that somebody may
                // be a request that is about to record its generation. Any
                // committed write to this row before that request saves makes
                // its save fail on the row version, so a note taken here turns
                // a concurrent upload that succeeded into a refusal and
                // removes the bytes it had already stored.
                //
                // The store being unreachable is not recorded either. It is
                // the common failure and it establishes nothing about durable
                // bytes.
                if (capture.Status == AttachmentObjectCaptureStatus.Unidentified)
                {
                    await NoteUnreclaimedCustodyAsync(attachment);
                }

                return CaptureFailure(capture.Status, attachment.Reference.Value);
            }

            // Compensation is decided inside and runs outside. Inside, it sat
            // in a block whose own handler compensates as well, so a removal
            // that threw turned a clean refusal into an unexpected failure and
            // asked the store to remove the same generation twice; and in the
            // handlers it sat where an exception of its own replaces the one
            // being reported. Neither is reachable from here.
            UploadAttempt attempt = await RecordAsync(attachment, locator, cancellationToken);
            if (attempt.RequiresCompensation
                && !await CompensateAsync(locator, attachment.Reference.Value))
            {
                // A removal the store did not confirm, and a removal that
                // threw, end the same way: bytes under a key that stays taken.
                // The line each of them already wrote says so and is read by
                // nobody on a schedule, so the fact is put where the round
                // reads it from.
                await NoteUnreclaimedCustodyAsync(attachment);
            }

            if (attempt.Fault is { } fault)
            {
                ExceptionDispatchInfo.Capture(fault).Throw();
            }

            if (attempt.Completion == UploadCompletion.Refused)
            {
                return attempt.Refusal;
            }

            Response response = Response.From(attachment);
            logger.AttachmentReceived(
                response.Reference,
                attachment.Application,
                response.State,
                attachment.SizeBytes);
            return Result.Success(response);
        }

        private async Task<UploadAttempt> RecordAsync(
            Attachment attachment,
            AttachmentObjectLocator locator,
            CancellationToken cancellationToken)
        {
            try
            {
                // The bytes that count are the ones the store kept, so they are
                // measured while the pinned generation is read back. What the
                // write counted describes only what left this process.
                AttachmentContentReading reading = await AttachmentContentVerification
                    .ComputeAsync(objectStore, locator, cancellationToken);
                if (reading is not
                    {
                        Status: AttachmentContentReadingStatus.Measured,
                        Proof: { } verified,
                    })
                {
                    return UploadAttempt.Refused(
                        ReadingFailure(reading.Status, attachment.Reference.Value),
                        compensate: true);
                }

                // The record of the generation and the state it justifies
                // become durable together, in one transaction. Splitting them
                // would create a durable attachment that is still waiting for
                // an upload while a generation is already recorded for it, and
                // nothing downstream has a reading for that state.
                DateTimeOffset receivedAt = timeProvider.GetUtcNow();
                AttachmentReceiveOutcome transition = attachment.MarkReceived(
                    verified.LengthBytes,
                    receivedAt);
                if (transition == AttachmentReceiveOutcome.SizeMismatch)
                {
                    return UploadAttempt.Refused(
                        Result.ValidationError<Response>(ErrorCodes.SizeMismatch),
                        compensate: true);
                }

                // Unreachable today, because the guard on the loaded state
                // already returned and nothing between the two reloads the
                // tracked instance. It has no runtime falsifier and is not
                // presented as one. It stays, and it compensates like the
                // branch above it, because the day the state is reread inside
                // the transaction is the day a branch without compensation
                // starts leaving bytes behind by construction.
                if (transition == AttachmentReceiveOutcome.AlreadyReceived)
                {
                    return UploadAttempt.Refused(
                        Result.BusinessRuleViolation<Response>(ErrorCodes.AlreadyReceived),
                        compensate: true);
                }

                // The recognized type travels with the proof because both were
                // measured over the same bytes in the same pass. Nothing here
                // compares it to what the producer declared: the policy is
                // evaluated once, at validation, and an upload that applied it
                // would make every later change to the admitted list reach back
                // and change what was already accepted.
                dbContext.ObjectGenerations.Add(AttachmentObjectGeneration.Capture(
                    attachment.Id,
                    locator,
                    verified,
                    reading.DetectedContentType,
                    receivedAt));
                await saveOperation.SaveChangesAsync(dbContext, cancellationToken);
                logger.AttachmentGenerationRecorded(
                    attachment.Reference.Value,
                    verified.LengthBytes,
                    locator);
                return UploadAttempt.Recorded();
            }
            catch (DbUpdateConcurrencyException exception)
            {
                DurableAttachmentState durableState = await ReadDurableStateAsync(
                    attachment.Reference);
                if (durableState == DurableAttachmentState.Received)
                {
                    return UploadAttempt.Refused(
                        Result.BusinessRuleViolation<Response>(ErrorCodes.AlreadyReceived),
                        compensate: false);
                }

                return durableState == DurableAttachmentState.AwaitingOrMissing
                    ? UploadAttempt.Refused(
                        Result.BusinessRuleViolation<Response>(ErrorCodes.UploadConflict),
                        compensate: true)
                    : UploadAttempt.Faulted(exception, compensate: false);
            }
            catch (Exception exception)
            {
                DurableAttachmentState durableState = await ReadDurableStateAsync(
                    attachment.Reference);
                return UploadAttempt.Faulted(
                    exception,
                    durableState == DurableAttachmentState.AwaitingOrMissing);
            }
        }

        /// <summary>
        /// Removes the generation this upload will not keep, and answers
        /// whether the store confirmed it. The answer is what the caller
        /// decides by: a removal nobody confirmed is a removal that did not
        /// happen, and the bytes go on the record as still stored.
        /// </summary>
        private async Task<bool> CompensateAsync(
            AttachmentObjectLocator locator,
            string reference)
        {
            using var deadline = new CancellationTokenSource(CompensationBudget);
            try
            {
                // The store answers whether it removed the generation, and a
                // removal it did not confirm leaves durable bytes under a key
                // that stays taken, so every retry is refused. The reference
                // is what makes those bytes findable again: the key derives
                // from the attachment and the generation derives from nothing.
                if (await objectStore.DiscardAsync(locator, deadline.Token)
                    == AttachmentObjectDiscard.Removed)
                {
                    return true;
                }

                logger.AttachmentGenerationNotRemoved(reference);
                return false;
            }
            catch (Exception exception)
            {
                // A removal that throws must not become the answer. The
                // failure it was compensating for is the one the caller has to
                // hear, so this one is recorded and stops here.
                logger.AttachmentCompensationFailed(exception, reference);
                return false;
            }
        }

        /// <summary>
        /// Records that this attachment's key holds bytes the record does not
        /// account for, so the round that repairs them has a row to find.
        /// <para>
        /// Every caller reached here after this request's own write placed the
        /// bytes, which the conditional write makes exclusive: no other
        /// request can have placed any under this key, and none can be about
        /// to record a generation for it. That exclusivity is what makes the
        /// note safe to take, because any committed write to this row makes a
        /// concurrent upload's save fail on the row version.
        /// </para>
        /// <para>
        /// It never becomes the answer to the caller. This runs after a
        /// failure that has already been decided, on a deadline of its own,
        /// and a failure of its own is a line: what it costs is that the bytes
        /// stay outside the backlog with nothing left to discover them.
        /// </para>
        /// </summary>
        private async Task NoteUnreclaimedCustodyAsync(Attachment attachment)
        {
            using var deadline = new CancellationTokenSource(LiabilityBudget);
            try
            {
                await using AttachmentManagementDbContext durableContext =
                    await dbContextFactory.CreateDbContextAsync(deadline.Token);
                if (await AttachmentLiabilityLedger.RecordAsync(
                    durableContext,
                    attachment.Reference,
                    AttachmentLiabilities.CustodyUnreclaimed,
                    deadline.Token))
                {
                    logger.AttachmentCustodyUnreclaimed(attachment.Reference.Value);
                }
            }
            catch (Exception exception)
            {
                logger.AttachmentLiabilityNotRecorded(exception, attachment.Reference.Value);
            }
        }

        private Result<Response> CaptureFailure(
            AttachmentObjectCaptureStatus status,
            string reference)
        {
            if (status == AttachmentObjectCaptureStatus.Unidentified)
            {
                // The write went through, so the bytes are durable, and the
                // store named no generation, so nothing here can remove them:
                // a removal needs the exact generation, and a removal without
                // one places a delete marker and leaves the bytes readable.
                // What is left is to say so and to name the attachment, which
                // is what a later reconciliation has to start from.
                logger.AttachmentBytesLeftWithoutIdentity(reference);
                return Result.IntegrationFailure<Response>(
                    ErrorCodes.StoreUnidentifiedGeneration);
            }

            return status switch
            {
                AttachmentObjectCaptureStatus.AlreadyExists =>
                    Result.BusinessRuleViolation<Response>(ErrorCodes.UploadConflict),

                // The transport dropped the write because the request sent
                // fewer bytes than it declared. That is the caller's size, not
                // the store's health.
                AttachmentObjectCaptureStatus.ContentShorterThanDeclared =>
                    Result.ValidationError<Response>(ErrorCodes.SizeMismatch),
                _ => Result.IntegrationFailure<Response>(ErrorCodes.StoreUnavailable),
            };
        }

        private Result<Response> ReadingFailure(
            AttachmentContentReadingStatus status,
            string reference)
        {
            if (status == AttachmentContentReadingStatus.Missing)
            {
                // Absence right after a write the store confirmed, read back
                // by the exact generation the store named, is not the store
                // being unreachable. The two answers send whoever reads them
                // to different places.
                logger.AttachmentGenerationVanished(reference);
                return Result.IntegrationFailure<Response>(ErrorCodes.GenerationUnreadable);
            }

            return Result.IntegrationFailure<Response>(ErrorCodes.StoreUnavailable);
        }

        private async Task<DurableAttachmentState> ReadDurableStateAsync(
            AttachmentReference reference)
        {
            try
            {
                await using AttachmentManagementDbContext durableContext =
                    await dbContextFactory.CreateDbContextAsync(CancellationToken.None);
                var state = await durableContext.Attachments
                    .AsNoTracking()
                    .Where(candidate => candidate.Reference == reference)
                    .Select(candidate => candidate.State)
                    .SingleOrDefaultAsync(CancellationToken.None);
                return state == AttachmentStates.Received
                    ? DurableAttachmentState.Received
                    : DurableAttachmentState.AwaitingOrMissing;
            }
            catch
            {
                logger.AttachmentCommitStateUnconfirmed(reference.Value);
                return DurableAttachmentState.Unconfirmed;
            }
        }
    }

    private enum DurableAttachmentState
    {
        AwaitingOrMissing,
        Received,
        Unconfirmed,
    }

    private enum UploadCompletion
    {
        Recorded,
        Refused,
        Faulted,
    }

    /// <summary>
    /// What the durable attempt ended as, and whether the generation it wrote
    /// still has to be removed. Carrying the decision out instead of acting on
    /// it inside is what keeps a removal from replacing the diagnosis.
    /// </summary>
    private sealed record UploadAttempt(
        UploadCompletion Completion,
        Result<Response> Refusal,
        Exception? Fault,
        bool RequiresCompensation)
    {
        internal static UploadAttempt Recorded()
            => new(UploadCompletion.Recorded, default, null, RequiresCompensation: false);

        internal static UploadAttempt Refused(Result<Response> refusal, bool compensate)
            => new(UploadCompletion.Refused, refusal, null, compensate);

        internal static UploadAttempt Faulted(Exception fault, bool compensate)
            => new(UploadCompletion.Faulted, default, fault, compensate);
    }
}
