using Microsoft.Extensions.Options;
using NotificationHub.Api.Modules.AttachmentManagement.Domain;
using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Capacity;
using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Persistence;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.AttachmentManagement.Features.Attachments;

internal static partial class RegisterAttachment
{
    internal sealed class Handler(
        AttachmentManagementDbContext dbContext,
        IAttachmentSaveOperation saveOperation,
        IOptions<AttachmentCapacityOptions> capacity,
        TimeProvider timeProvider,
        ILogger<Handler> logger)
    {
        public async Task<Result<Response>> HandleAsync(
            Command command,
            CancellationToken cancellationToken)
        {
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

            Response response = Response.From(attachment);
            logger.AttachmentRegistered(
                response.Reference,
                attachment.Application,
                response.State,
                attachment.SizeBytes);
            return Result.Success(response);
        }
    }
}
