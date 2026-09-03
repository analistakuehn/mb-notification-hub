using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NotificationHub.Api.Modules.AttachmentManagement.Domain;
using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Persistence;
using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Validation;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.AttachmentManagement.Features.Operations;

internal static partial class GetAttachmentLifecycle
{
    /// <summary>
    /// The authorized reading of what the lifecycle left durable. It is the
    /// reader the fine detail of a refusal was kept for, and the only observer
    /// of a deadline that no column holds.
    /// </summary>
    internal sealed class Handler(
        AttachmentManagementDbContext dbContext,
        IOptions<AttachmentValidationOptions> options)
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
            Attachment? attachment = await dbContext.Attachments
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    candidate => candidate.Reference == reference,
                    cancellationToken);
            if (attachment is null)
            {
                return Result.NotFound<Response>(ErrorCodes.NotFound);
            }

            // The grant in force is the most recent one, read the same way the
            // act that takes one back reads it, so the two never disagree about
            // which release the answer is about.
            AttachmentRelease? release = await dbContext.Releases
                .AsNoTracking()
                .Where(candidate => candidate.AttachmentId == attachment.Id)
                .OrderByDescending(candidate => candidate.ReleasedAt)
                .ThenByDescending(candidate => candidate.Id)
                .FirstOrDefaultAsync(cancellationToken);
            AttachmentRevocation? revocation = release is null
                ? null
                : await dbContext.Revocations
                    .AsNoTracking()
                    .SingleOrDefaultAsync(
                        candidate => candidate.ReleaseId == release.Id,
                        cancellationToken);

            AttachmentValidationOptions settings = options.Value;
            return Result.Success(new Response(
                attachment.Reference.Value,
                attachment.State,
                attachment.ValidationDetail,
                attachment.InconclusiveUntil,
                release?.ReleasedAt,
                release?.DeadlineAt(settings.ReleaseValidity, settings.ValidityEffectiveFrom),
                revocation?.RevokedAt,
                revocation?.Reason));
        }
    }
}
