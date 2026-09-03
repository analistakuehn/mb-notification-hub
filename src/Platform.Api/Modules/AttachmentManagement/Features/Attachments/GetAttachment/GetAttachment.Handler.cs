using Microsoft.EntityFrameworkCore;
using NotificationHub.Api.Modules.AttachmentManagement.Domain;
using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Persistence;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.AttachmentManagement.Features.Attachments;

internal static partial class GetAttachment
{
    internal sealed class Handler(AttachmentManagementDbContext dbContext)
    {
        public async Task<Result<Response>> HandleAsync(
            Request request,
            CancellationToken cancellationToken)
        {
            Result<AttachmentReference> reference = AttachmentReference.Create(request.Reference);
            if (reference.IsFailure)
            {
                return Result.NotFound<Response>(ErrorCodes.NotFound);
            }

            Attachment? attachment = await dbContext.Attachments
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    candidate => candidate.Reference == reference.Value,
                    cancellationToken);
            return attachment is null
                ? Result.NotFound<Response>(ErrorCodes.NotFound)
                : Result.Success(Response.From(attachment));
        }
    }
}
