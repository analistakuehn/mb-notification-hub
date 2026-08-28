using Microsoft.EntityFrameworkCore;
using Npgsql;
using NotificationHub.Api.Modules.TemplateManagement.Domain;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.ErrorHandling;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Http;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Persistence;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.TemplateManagement.Features.ClassPolicies;

internal static partial class PutClassPolicyDraft
{
    internal sealed class Handler(
        TemplateManagementDbContext dbContext,
        TimeProvider timeProvider,
        ILogger<Handler> logger)
    {
        public async Task<Result<Outcome>> HandleAsync(Command command, CancellationToken cancellationToken)
        {
            Result<string> application = ApplicationName.Create(command.Route.Application);
            if (application.IsFailure)
            {
                return application.AsFailure<string, Outcome>();
            }

            Result<NotificationClass> policyClass = NotificationClasses.Create(command.Route.Class);
            if (policyClass.IsFailure)
            {
                return policyClass.AsFailure<NotificationClass, Outcome>();
            }

            var app = application.Value!;
            NotificationClass notificationClass = policyClass.Value;
            ClassPolicyVersion? draft = await dbContext.ClassPolicyVersions
                .Where(candidate => candidate.Application == app
                    && candidate.Class == notificationClass
                    && candidate.Status == ClassPolicyVersionStatus.Draft)
                .FirstOrDefaultAsync(cancellationToken);

            if (draft is not null)
            {
                return await UpdateDraftAsync(draft, command, cancellationToken);
            }

            if (!string.IsNullOrWhiteSpace(command.Route.IfMatch))
            {
                return Result.BusinessRuleViolation<Outcome>(DomainError.Format(
                    ErrorCodes.PreconditionFailed,
                    "There is no open draft to match. Omit the If-Match header to open a new draft."));
            }

            return await CreateDraftAsync(app, notificationClass, command, cancellationToken);
        }

        private async Task<Result<Outcome>> UpdateDraftAsync(
            ClassPolicyVersion draft,
            Command command,
            CancellationToken cancellationToken)
        {
            Result precondition = EntityTags.CheckIfMatch(command.Route.IfMatch, draft.EntityTag);
            if (precondition.IsFailure)
            {
                return precondition.AsFailure<Outcome>();
            }

            Outcome? blocked = ValidateDefinition(command.DefinitionJson, draft.Application, draft.Class.Canonical());
            if (blocked is not null)
            {
                return Result.Success<Outcome>(blocked);
            }

            Result edited = draft.SetDefinition(command.DefinitionJson, command.Actor);
            if (edited.IsFailure)
            {
                return edited.AsFailure<Outcome>();
            }

            try
            {
                await dbContext.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                return Result.BusinessRuleViolation<Outcome>(DomainError.Format(
                    ErrorCodes.PreconditionFailed,
                    "The draft changed while this edit was in flight. "
                    + "Fetch it again and retry with the current entity tag."));
            }

            var canonicalClass = draft.Class.Canonical();
            logger.ClassPolicyDraftUpdated(draft.Application, canonicalClass, draft.Version);
            return Result.Success<Outcome>(new Outcome.Updated(Response.From(draft)));
        }

        private async Task<Result<Outcome>> CreateDraftAsync(
            string application,
            NotificationClass notificationClass,
            Command command,
            CancellationToken cancellationToken)
        {
            var canonicalClass = notificationClass.Canonical();
            Outcome? blocked = ValidateDefinition(command.DefinitionJson, application, canonicalClass);
            if (blocked is not null)
            {
                return Result.Success<Outcome>(blocked);
            }

            var nextVersion = (await dbContext.ClassPolicyVersions
                .AsNoTracking()
                .Where(candidate => candidate.Application == application && candidate.Class == notificationClass)
                .MaxAsync(candidate => (int?)candidate.Version, cancellationToken) ?? 0) + 1;

            Result<ClassPolicyVersion> draft = ClassPolicyVersion.CreateDraft(new ClassPolicyDraftInput
            {
                Application = application,
                Class = notificationClass,
                Version = nextVersion,
                DefinitionJson = command.DefinitionJson,
                CreatedBy = command.Actor,
                CreatedAt = timeProvider.GetUtcNow(),
            });
            if (draft.IsFailure)
            {
                return draft.AsFailure<ClassPolicyVersion, Outcome>();
            }

            dbContext.ClassPolicyVersions.Add(draft.Value!);
            try
            {
                await dbContext.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException exception)
                when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                return Result.BusinessRuleViolation<Outcome>(DomainError.Format(
                    ErrorCodes.DraftAlreadyExists,
                    $"A policy draft for application '{application}' and class "
                    + $"'{canonicalClass}' was opened concurrently. "
                    + "Fetch it and retry with its current entity tag."));
            }

            logger.ClassPolicyDraftOpened(application, canonicalClass, nextVersion);
            return Result.Success<Outcome>(new Outcome.Created(Response.From(draft.Value!)));
        }

        /// <summary>
        /// The report is a response value: a failed check blocks the save, but
        /// running the validation is a successful execution of the use case.
        /// </summary>
        private Outcome.Blocked? ValidateDefinition(string definitionJson, string application, string canonicalClass)
        {
            ValidationReport report = ClassPolicyValidation.Validate(definitionJson);
            if (report.Passed)
            {
                return null;
            }

            var failed = report.Checks.Count(check => check.Status == ValidationCheckStatuses.Failed);
            logger.ClassPolicyDraftBlocked(application, canonicalClass, failed);
            return new Outcome.Blocked(report);
        }
    }
}
