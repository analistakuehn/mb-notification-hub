using NotificationHub.Api.Modules.AttachmentManagement.Domain;
using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Revocation;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.AttachmentManagement.Features.Attachments;

internal static partial class RevokeAttachment
{
    /// <summary>
    /// The production path into the act that takes a release back. Repeating
    /// it answers what the call that took the release back answered, and the
    /// second call writes nothing: the withdrawal keeps the instant and the
    /// reason it was performed with, so a retry after a lost response reports
    /// the state without rewriting the record of how it was reached.
    /// </summary>
    internal sealed class Handler(AttachmentRevocationOperation revocation)
    {
        public async Task<Result<Response>> HandleAsync(
            string reference,
            Command command,
            CancellationToken cancellationToken)
        {
            Result<AttachmentReference> parsed = AttachmentReference.Create(reference);
            if (parsed.IsFailure)
            {
                return Result.NotFound<Response>(ErrorCodes.NotFound);
            }

            AttachmentRevocationStatus status = await revocation.RevokeAsync(
                parsed.Value!,
                command.Reason,
                cancellationToken);
            return status switch
            {
                // One answer for the act and for its repeat. The caller asked
                // for a state, the attachment carries it, and which of the two
                // calls put it there is not something a caller can act on.
                AttachmentRevocationStatus.Revoked
                    or AttachmentRevocationStatus.AlreadyRevoked =>
                    Result.Success(new Response(parsed.Value!.Value, AttachmentStates.Revoked)),
                AttachmentRevocationStatus.NotReleased =>
                    Result.BusinessRuleViolation<Response>(ErrorCodes.NotReleased),
                AttachmentRevocationStatus.InvalidReason =>
                    Result.ValidationError<Response>(ErrorCodes.InvalidMetadata),
                AttachmentRevocationStatus.UnknownAttachment =>
                    Result.NotFound<Response>(ErrorCodes.NotFound),

                // The grant the module cannot name. Nothing was written, and
                // nothing is deliverable either, because what a later check
                // reads is the release and there is none. It is also the arm
                // the compiler needs for a value the enum does not carry today.
                _ => Result.IntegrationFailure<Response>(ErrorCodes.LifecycleUnavailable),
            };
        }
    }
}
