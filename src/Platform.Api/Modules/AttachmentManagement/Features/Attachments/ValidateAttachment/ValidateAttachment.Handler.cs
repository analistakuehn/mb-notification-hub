using Microsoft.EntityFrameworkCore;
using NotificationHub.Api.Modules.AttachmentManagement.Domain;
using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Persistence;
using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Validation;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.AttachmentManagement.Features.Attachments;

internal static partial class ValidateAttachment
{
    /// <summary>
    /// The production path into the state machine. Nothing else asks for a
    /// verdict: the upload records what the bytes were recognized as and stops
    /// there, and the check made before a message goes out reads the release
    /// and never the policy.
    /// <para>
    /// It maps one outcome to one public answer, and the whole family of
    /// content refusals leaves under a single word. Repeating the request over
    /// an attachment that is already settled asks the machine for nothing,
    /// writes nothing, and answers exactly what the call that settled it
    /// answered, so a producer that lost a response learns the same thing by
    /// sending it again.
    /// </para>
    /// </summary>
    internal sealed class Handler(
        AttachmentValidation validation,
        AttachmentManagementDbContext dbContext)
    {
        public async Task<Result<Response>> HandleAsync(
            Request request,
            CancellationToken cancellationToken)
        {
            Result<AttachmentReference> parsed = AttachmentReference.Create(request.Reference);
            if (parsed.IsFailure)
            {
                return Result.NotFound<Response>(ErrorCodes.NotFound);
            }

            AttachmentReference reference = parsed.Value!;
            AttachmentValidationOutcome outcome = await validation.ValidateAsync(
                reference,
                cancellationToken);
            return outcome.Status switch
            {
                AttachmentValidationStatus.Released =>
                    Answer(reference, AttachmentStates.Released),
                AttachmentValidationStatus.Inconclusive =>
                    Answer(reference, AttachmentStates.Inconclusive),
                AttachmentValidationStatus.Rejected =>
                    Result.BusinessRuleViolation<Response>(ErrorCodes.ContentRefused),
                AttachmentValidationStatus.NotReceived =>
                    Result.BusinessRuleViolation<Response>(ErrorCodes.ContentMissing),
                AttachmentValidationStatus.AlreadyDecided =>
                    await SettledAnswerAsync(reference, cancellationToken),
                AttachmentValidationStatus.UnknownAttachment =>
                    Result.NotFound<Response>(ErrorCodes.NotFound),

                // The identity the module cannot name and the policy that did
                // not decide. Both wrote nothing, both leave the attachment as
                // unreleased as it was, and both are one word here because the
                // difference between them is on the record and is not a
                // producer's business. It is also the arm the compiler needs
                // for a value the enum does not carry today.
                _ => Result.IntegrationFailure<Response>(ErrorCodes.LifecycleUnavailable),
            };
        }

        /// <summary>
        /// What a repeat is told. The state is read after the machine answered
        /// that it decided nothing, so the answer is the one the call that
        /// settled the attachment gave, and a request that changed nothing
        /// reports nothing new.
        /// </summary>
        private async Task<Result<Response>> SettledAnswerAsync(
            AttachmentReference reference,
            CancellationToken cancellationToken)
        {
            var state = await dbContext.Attachments
                .AsNoTracking()
                .Where(attachment => attachment.Reference == reference)
                .Select(attachment => attachment.State)
                .SingleAsync(cancellationToken);
            return state switch
            {
                AttachmentStates.Released => Answer(reference, AttachmentStates.Released),
                AttachmentStates.Rejected =>
                    Result.BusinessRuleViolation<Response>(ErrorCodes.ContentRefused),
                AttachmentStates.Revoked =>
                    Result.BusinessRuleViolation<Response>(ErrorCodes.Revoked),

                // Unreachable: the machine answers that it decided nothing
                // only for the three states above, and the row it read them
                // from was held for the whole of its transaction. It has no
                // runtime falsifier and is not presented as a proven branch.
                // It stays because the day a fourth settled state exists, an
                // answer that fell through to a release would be the worst one
                // this switch could give.
                _ => Result.IntegrationFailure<Response>(ErrorCodes.LifecycleUnavailable),
            };
        }

        private static Result<Response> Answer(AttachmentReference reference, string state)
            => Result.Success(new Response(reference.Value, state));
    }
}
