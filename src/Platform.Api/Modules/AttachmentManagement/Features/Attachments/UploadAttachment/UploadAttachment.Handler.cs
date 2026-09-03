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
                return CaptureFailure(capture.Status, attachment.Reference.Value);
            }

            // Compensation is decided inside and runs outside. Inside, it sat
            // in a block whose own handler compensates as well, so a removal
            // that threw turned a clean refusal into an unexpected failure and
            // asked the store to remove the same generation twice; and in the
            // handlers it sat where an exception of its own replaces the one
            // being reported. Neither is reachable from here.
            UploadAttempt attempt = await RecordAsync(attachment, locator, cancellationToken);
            if (attempt.RequiresCompensation)
            {
                await CompensateAsync(locator, attachment.Reference.Value);
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

        private async Task CompensateAsync(AttachmentObjectLocator locator, string reference)
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
                    != AttachmentObjectDiscard.Removed)
                {
                    logger.AttachmentGenerationNotRemoved(reference);
                }
            }
            catch (Exception exception)
            {
                // A removal that throws must not become the answer. The
                // failure it was compensating for is the one the caller has to
                // hear, so this one is recorded and stops here.
                logger.AttachmentCompensationFailed(exception, reference);
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
