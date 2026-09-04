using Microsoft.Extensions.Options;
using NotificationHub.Api.Modules.AttachmentManagement.Domain;
using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Capability;
using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Capacity;
using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Persistence;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.AttachmentManagement.Features.Attachments;

internal static partial class RegisterAttachment
{
    internal sealed class Handler(
        AttachmentManagementDbContext dbContext,
        IAttachmentSaveOperation saveOperation,
        AttachmentCapability capability,
        IOptions<AttachmentCapacityOptions> capacity,
        TimeProvider timeProvider,
        ILogger<Handler> logger)
    {
        public async Task<Result<Response>> HandleAsync(
            Command command,
            CancellationToken cancellationToken)
        {
            // The first of the two doors a new attachment passes, and the
            // earlier one: nothing is minted, nothing is written and no
            // ceiling is even read while the capability is not switched on
            // here. It is asked before the metadata is judged so a producer
            // is never told its file is wrong when the truth is that nothing
            // would have been accepted anyway.
            if (!capability.AcceptsNewAttachments)
            {
                logger.AttachmentRegistrationNotEnabled(command.Application);
                return Result.BusinessRuleViolation<Response>(ErrorCodes.CapabilityNotEnabled);
            }

            Result<Attachment> registration = Attachment.Register(
                command.Application,
                command.FileName,
                command.ContentType,
                command.SizeBytes,
                capacity.Value.MaxAttachmentBytes,
                timeProvider.GetUtcNow());
            if (registration.IsFailure)
            {
                return Result.ValidationError<Response>(
                    registration.Error ?? ErrorCodes.InvalidMetadata);
            }

            Attachment attachment = registration.Value!;
            dbContext.Attachments.Add(attachment);
            await saveOperation.SaveChangesAsync(dbContext, cancellationToken);

            var response = Response.From(attachment);
            logger.AttachmentRegistered(
                response.Reference,
                attachment.Application,
                response.State,
                attachment.SizeBytes);
            return Result.Success(response);
        }
    }
}
