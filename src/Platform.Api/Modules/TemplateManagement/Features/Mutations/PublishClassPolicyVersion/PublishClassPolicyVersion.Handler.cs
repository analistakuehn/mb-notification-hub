using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NotificationHub.Api.Modules.TemplateManagement.Domain;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.ErrorHandling;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Persistence;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.TemplateManagement.Features.Mutations;

internal static partial class PublishClassPolicyVersion
{
    internal sealed class Handler(
        TemplateManagementDbContext dbContext,
        TimeProvider timeProvider,
        ILogger<Handler> logger)
    {
        public async Task<Result<Outcome>> HandleAsync(
            string applicationValue,
            string classValue,
            string publisher,
            CancellationToken cancellationToken)
        {
            Result<string> application = ApplicationName.Create(applicationValue);
            if (application.IsFailure)
            {
                return application.AsFailure<string, Outcome>();
            }

            Result<NotificationClass> policyClass = NotificationClasses.Create(classValue);
            if (policyClass.IsFailure)
            {
                return policyClass.AsFailure<NotificationClass, Outcome>();
            }

            var app = application.Value!;
            NotificationClass notificationClass = policyClass.Value;
            var canonicalClass = notificationClass.Canonical();
            ClassPolicyVersion? draft = await dbContext.ClassPolicyVersions
                .Where(candidate => candidate.Application == app
                    && candidate.Class == notificationClass
                    && candidate.Status == ClassPolicyVersionStatus.Draft)
                .FirstOrDefaultAsync(cancellationToken);
            if (draft is null)
            {
                return Result.NotFound<Outcome>(DomainError.Format(
                    ErrorCodes.ClassPolicyDraftNotFound,
                    $"Application '{app}' has no open policy draft for class "
                    + $"'{canonicalClass}' to publish."));
            }

            Result eligibility = draft.CanBePublishedBy(publisher);
            if (eligibility.IsFailure)
            {
                return eligibility.AsFailure<Outcome>();
            }

            // Same structural validation the draft endpoint applies: a
            // definition only publishes after passing it again, in full,
            // right now.
            ValidationReport report = ClassPolicyValidation.Validate(draft.DefinitionJson);
            if (!report.Passed)
            {
                var failed = report.Checks.Count(check => check.Status == ValidationCheckStatuses.Failed);
                logger.ClassPolicyPublicationBlocked(app, canonicalClass, draft.Version, failed);
                return Result.Success<Outcome>(new Outcome.Blocked(report));
            }

            Result integrity = draft.VerifyContentHash();
            if (integrity.IsFailure)
            {
                return integrity.AsFailure<Outcome>();
            }

            ClassPolicyVersion? current = await dbContext.ClassPolicyVersions
                .Where(candidate => candidate.Application == app
                    && candidate.Class == notificationClass
                    && candidate.Status == ClassPolicyVersionStatus.Published)
                .FirstOrDefaultAsync(cancellationToken);

            DateTimeOffset now = timeProvider.GetUtcNow();
            Result published = draft.Publish(publisher, now);
            if (published.IsFailure)
            {
                return published.AsFailure<Outcome>();
            }

            if (current is not null)
            {
                Result superseded = current.Supersede();
                if (superseded.IsFailure)
                {
                    return superseded.AsFailure<Outcome>();
                }
            }

            dbContext.Approvals.Add(Approval.ForClassPolicyVersion(
                app,
                notificationClass,
                draft.Version,
                draft.ContentHash,
                publisher,
                now));
            dbContext.AuditEvents.Add(AuditEvent.Record(new AuditEntry
            {
                ActorType = AuditActorTypes.User,
                ActorId = publisher,
                Application = app,
                Action = AuditActions.ClassPolicyVersionPublished,
                EntityType = AuditEntityTypes.ClassPolicyVersion,
                EntityId = $"{app}:{canonicalClass}:{draft.Version}",
                DetailsJson = PublicationDetails(draft, report, current?.Version),
                OccurredAt = now,
            }));

            try
            {
                // One SaveChanges, one database transaction: the status flips,
                // the approval and the audit event land together or not at all.
                await dbContext.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                return Result.BusinessRuleViolation<Outcome>(DomainError.Format(
                    ErrorCodes.PreconditionFailed,
                    "The draft changed while the publication was in flight. Validate and publish again."));
            }

            logger.ClassPolicyVersionPublished(app, canonicalClass, draft.Version, current?.Version);
            return Result.Success<Outcome>(new Outcome.Published(Response.From(draft, current?.Version)));
        }

        private static string PublicationDetails(
            ClassPolicyVersion version,
            ValidationReport report,
            int? supersededVersion)
            => JsonSerializer.Serialize(new
            {
                contentHash = version.ContentHash,
                schemaVersion = version.SchemaVersion,
                supersededVersion,
                validation = new
                {
                    passed = report.Passed,
                    checks = report.Checks.Select(check => new
                    {
                        name = check.Name,
                        status = check.Status,
                        message = check.Message,
                        location = check.Location,
                    }).ToList(),
                },
            });
    }
}
