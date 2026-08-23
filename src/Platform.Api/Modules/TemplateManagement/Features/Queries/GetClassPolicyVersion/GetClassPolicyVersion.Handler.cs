using Microsoft.EntityFrameworkCore;
using NotificationHub.Api.Modules.TemplateManagement.Domain;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.ErrorHandling;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Persistence;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.TemplateManagement.Features.Queries;

internal static partial class GetClassPolicyVersion
{
    internal sealed class Handler(TemplateManagementDbContext dbContext)
    {
        public async Task<Result<Response>> HandleAsync(
            string applicationValue,
            string classValue,
            int versionNumber,
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
            ClassPolicyVersion? found = await dbContext.ClassPolicyVersions
                .AsNoTracking()
                .Where(candidate => candidate.Application == app && candidate.Class == notificationClass)
                .FirstOrDefaultAsync(candidate => candidate.Version == versionNumber, cancellationToken);
            if (found is null)
            {
                return Result.NotFound<Response>(DomainError.Format(
                    ErrorCodes.ClassPolicyVersionNotFound,
                    $"The policy of application '{app}' and class '{notificationClass.Canonical()}' "
                    + $"has no version {versionNumber}."));
            }

            return Result.Success(Response.From(found));
        }
    }
}
