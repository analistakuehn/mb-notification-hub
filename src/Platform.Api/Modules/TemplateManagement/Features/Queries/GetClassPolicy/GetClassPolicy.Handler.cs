using Microsoft.EntityFrameworkCore;
using NotificationHub.Api.Modules.TemplateManagement.Domain;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.ErrorHandling;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Persistence;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.TemplateManagement.Features.Queries;

internal static partial class GetClassPolicy
{
    internal sealed class Handler(TemplateManagementDbContext dbContext)
    {
        public async Task<Result<Response>> HandleAsync(
            string applicationValue,
            string classValue,
            CancellationToken cancellationToken)
        {
            Result<string> application = ApplicationName.Create(applicationValue);
            if (application.IsFailure)
            {
                return application.AsFailure<string, Response>();
            }

            Result<NotificationClass> policyClass = NotificationClasses.Create(classValue);
            if (policyClass.IsFailure)
            {
                return policyClass.AsFailure<NotificationClass, Response>();
            }

            var app = application.Value!;
            NotificationClass notificationClass = policyClass.Value;
            List<ClassPolicyVersion> versions = await dbContext.ClassPolicyVersions
                .AsNoTracking()
                .Where(candidate => candidate.Application == app
                    && candidate.Class == notificationClass
                    && (candidate.Status == ClassPolicyVersionStatus.Published
                        || candidate.Status == ClassPolicyVersionStatus.Draft))
                .ToListAsync(cancellationToken);
            if (versions.Count == 0)
            {
                return Result.NotFound<Response>(DomainError.Format(
                    ErrorCodes.ClassPolicyNotFound,
                    $"Application '{app}' has no policy for class '{notificationClass.Canonical()}'."));
            }

            ClassPolicyVersion? published = versions
                .FirstOrDefault(version => version.Status == ClassPolicyVersionStatus.Published);
            ClassPolicyVersion? draft = versions
                .FirstOrDefault(version => version.Status == ClassPolicyVersionStatus.Draft);
            return Result.Success(new Response
            {
                Application = app,
                Class = notificationClass.Canonical(),
                Published = published is null ? null : VersionDetail.From(published),
                Draft = draft is null ? null : VersionDetail.From(draft),
                DraftEntityTag = draft?.EntityTag,
            });
        }
    }
}
