using Microsoft.EntityFrameworkCore;
using NotificationHub.Api.Modules.TemplateManagement.Domain;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.ErrorHandling;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Persistence;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.TemplateManagement.Features.ClassPolicies;

internal static partial class ValidateClassPolicyVersion
{
    internal sealed class Handler(
        TemplateManagementDbContext dbContext,
        ILogger<Handler> logger)
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
            var canonicalClass = notificationClass.Canonical();
            ClassPolicyVersion? found = await dbContext.ClassPolicyVersions
                .AsNoTracking()
                .Where(candidate => candidate.Application == app && candidate.Class == notificationClass)
                .FirstOrDefaultAsync(candidate => candidate.Version == versionNumber, cancellationToken);
            if (found is null)
            {
                return Result.NotFound<Response>(DomainError.Format(
                    ErrorCodes.ClassPolicyVersionNotFound,
                    $"The policy of application '{app}' and class '{canonicalClass}' "
                    + $"has no version {versionNumber}."));
            }

            // The report is the value this use case produces: running the
            // validation succeeds even when checks fail, so failed checks
            // travel in the response, never in the error string.
            ValidationReport report = ClassPolicyValidation.Validate(found.DefinitionJson);
            var failed = report.Checks.Count(check => check.Status == ValidationCheckStatuses.Failed);
            logger.ClassPolicyVersionValidated(
                app, canonicalClass, found.Version, report.Passed, failed);
            return Result.Success(Response.From(report));
        }
    }
}
