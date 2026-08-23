using Microsoft.EntityFrameworkCore;
using NotificationHub.Api.Modules.TemplateManagement.Domain;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.ErrorHandling;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Persistence;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.TemplateManagement.Features.Queries;

internal static partial class DiffClassPolicyVersions
{
    internal sealed class Handler(TemplateManagementDbContext dbContext)
    {
        public async Task<Result<Response>> HandleAsync(Query query, CancellationToken cancellationToken)
        {
            Result<string> application = ApplicationName.Create(query.Application);
            if (application.IsFailure)
            {
                return application.AsFailure<string, Response>();
            }

            Result<NotificationClass> policyClass = NotificationClasses.Create(query.Class);
            if (policyClass.IsFailure)
            {
                return policyClass.AsFailure<NotificationClass, Response>();
            }

            var app = application.Value!;
            NotificationClass notificationClass = policyClass.Value;
            ClassPolicyVersion? baseVersion = await FindVersionAsync(
                app, notificationClass, query.Version, cancellationToken);
            if (baseVersion is null)
            {
                return VersionNotFound(app, notificationClass, query.Version);
            }

            ClassPolicyVersion? against = await FindVersionAsync(
                app, notificationClass, query.AgainstVersion, cancellationToken);
            if (against is null)
            {
                return VersionNotFound(app, notificationClass, query.AgainstVersion);
            }

            SchemaFieldDiff definition = VersionDiff.DiffObjectFields(
                baseVersion.DefinitionJson,
                against.DefinitionJson);
            return Result.Success(Response.From(
                app, notificationClass, query.Version, query.AgainstVersion, definition));
        }

        private async Task<ClassPolicyVersion?> FindVersionAsync(
            string application,
            NotificationClass notificationClass,
            int versionNumber,
            CancellationToken cancellationToken)
            => await dbContext.ClassPolicyVersions
                .AsNoTracking()
                .Where(candidate => candidate.Application == application && candidate.Class == notificationClass)
                .FirstOrDefaultAsync(candidate => candidate.Version == versionNumber, cancellationToken);

        private static Result<Response> VersionNotFound(
            string application,
            NotificationClass notificationClass,
            int versionNumber)
            => Result.NotFound<Response>(DomainError.Format(
                ErrorCodes.ClassPolicyVersionNotFound,
                $"The policy of application '{application}' and class '{notificationClass.Canonical()}' "
                + $"has no version {versionNumber}."));
    }
}
