using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NotificationHub.Api.Modules.Audit.Integration.V1;
using NotificationHub.Api.Modules.TemplateManagement.Domain;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.ErrorHandling;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Integration;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Persistence;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.TemplateManagement.Features.ClassPolicies;

internal static partial class PublishClassPolicyVersion
{
    internal sealed class Handler(
        TemplateManagementDbContext dbContext,
        IAuditTrail auditTrail,
        PublishedReadCache cache,
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

            var grant = new ApprovalGrant
            {
                SubjectType = ApprovalSubjectTypes.ClassPolicyVersion,
                SubjectId = $"{app}:{canonicalClass}",
                SubjectVersion = draft.Version,
                ContentHash = draft.ContentHash,
                Role = ApprovalRoles.Publisher,
                ApproverOid = publisher,
                ApprovedAt = now,
            };
            var entry = new AuditEntry
            {
                ActorType = AuditActorTypes.User,
                ActorId = publisher,
                Application = app,
                Action = AuditActions.ClassPolicyVersionPublished,
                EntityType = AuditEntityTypes.ClassPolicyVersion,
                EntityId = $"{app}:{canonicalClass}:{draft.Version}",
                DetailsJson = PublicationAuditDetails.ForClassPolicyPublication(
                    draft.ContentHash, draft.SchemaVersion, current?.Version, report),
                OccurredAt = now,
            };

            // One database transaction shared with the audit contract: the
            // status flips, the approval and the audit event land together or
            // not at all.
            await using IDbContextTransaction transaction =
                await dbContext.Database.BeginTransactionAsync(cancellationToken);
            try
            {
                await dbContext.SaveChangesAsync(cancellationToken);
                await auditTrail.RecordApprovalAsync(transaction.GetDbTransaction(), grant, cancellationToken);
                await auditTrail.AppendAsync(transaction.GetDbTransaction(), entry, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                return Result.BusinessRuleViolation<Outcome>(DomainError.Format(
                    ErrorCodes.PreconditionFailed,
                    "The draft changed while the publication was in flight. Validate and publish again."));
            }
            catch (DbUpdateException exception)
                when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                return Result.BusinessRuleViolation<Outcome>(DomainError.Format(
                    ErrorCodes.PublicationConflict,
                    "Another publication for this class policy landed concurrently. "
                    + "Fetch the current state and retry if still applicable."));
            }

            // After the commit and after every exit above: the concurrency and
            // unique-violation paths return without reaching here, so a
            // publication that did not persist never drops what the store
            // still answers. Only this process is reached; every other one
            // keeps answering the previous policy until its own pointer
            // expires.
            cache.InvalidatePointer(PublishedPointerKeys.ClassPolicy(app, canonicalClass));
            logger.ClassPolicyVersionPublished(app, canonicalClass, draft.Version, current?.Version);
            return Result.Success<Outcome>(new Outcome.Published(Response.From(draft, current?.Version)));
        }
    }
}
