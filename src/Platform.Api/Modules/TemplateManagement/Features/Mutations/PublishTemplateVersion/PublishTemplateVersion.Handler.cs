using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NotificationHub.Api.Modules.TemplateManagement.Domain;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.ErrorHandling;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Persistence;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Templating;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.TemplateManagement.Features.Mutations;

internal static partial class PublishTemplateVersion
{
    internal sealed class Handler(
        TemplateManagementDbContext dbContext,
        TemplateVersionAnalyzer analyzer,
        TimeProvider timeProvider,
        ILogger<Handler> logger)
    {
        public async Task<Result<Outcome>> HandleAsync(
            string key,
            int versionNumber,
            string publisher,
            CancellationToken cancellationToken)
        {
            Result<TemplateKey> templateKey = TemplateKey.Create(key);
            if (templateKey.IsFailure)
            {
                return templateKey.AsFailure<TemplateKey, Outcome>();
            }

            // Tracked on purpose: the publication touches the template row in
            // the same transaction, so a concurrent lifecycle transition
            // invalidates this publication instead of slipping past the
            // status check below.
            Template? template = await dbContext.Templates
                .WhereKey(templateKey.Value!)
                .FirstOrDefaultAsync(cancellationToken);
            if (template is null)
            {
                return Result.NotFound<Outcome>(DomainError.Format(
                    ErrorCodes.TemplateNotFound,
                    $"Template '{templateKey.Value!.Value}' does not exist."));
            }

            Result accepting = template.EnsureAcceptsPublication();
            if (accepting.IsFailure)
            {
                return accepting.AsFailure<Outcome>();
            }

            TemplateVersion? version = await dbContext.TemplateVersions
                .WhereTemplateKey(templateKey.Value!)
                .FirstOrDefaultAsync(candidate => candidate.Version == versionNumber, cancellationToken);
            if (version is null)
            {
                return Result.NotFound<Outcome>(DomainError.Format(
                    ErrorCodes.TemplateVersionNotFound,
                    $"Template '{templateKey.Value!.Value}' has no version {versionNumber}."));
            }

            Result eligibility = version.CanBePublishedBy(publisher);
            if (eligibility.IsFailure)
            {
                return eligibility.AsFailure<Outcome>();
            }

            // Same validation catalog the authoring endpoints expose: a version
            // only publishes after passing it again, in full, right now.
            LayoutReferenceFacts? layoutReference =
                await dbContext.LoadLayoutReferenceAsync(version, cancellationToken);
            ValidationReport report = TemplateValidation.Validate(
                template, version, analyzer.Analyze(version), layoutReference);
            if (!report.Passed)
            {
                var failed = report.Checks.Count(check => check.Status == ValidationCheckStatuses.Failed);
                logger.PublicationBlocked(version.TemplateKey.Value, version.Version, failed);
                return Result.Success<Outcome>(new Outcome.Blocked(report));
            }

            Result integrity = version.VerifyContentHash();
            if (integrity.IsFailure)
            {
                return integrity.AsFailure<Outcome>();
            }

            TemplateVersion? current = await dbContext.TemplateVersions
                .WhereTemplateKey(templateKey.Value!)
                .Where(candidate => candidate.Status == TemplateVersionStatus.Published)
                .FirstOrDefaultAsync(cancellationToken);

            DateTimeOffset now = timeProvider.GetUtcNow();
            Result published = version.Publish(publisher, now);
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

            dbContext.Approvals.Add(Approval.ForTemplateVersion(
                templateKey.Value!,
                version.Version,
                version.ContentHash,
                publisher,
                now));
            dbContext.AuditEvents.Add(AuditEvent.Record(new AuditEntry
            {
                ActorType = AuditActorTypes.User,
                ActorId = publisher,
                Application = template.Application,
                Action = AuditActions.TemplateVersionPublished,
                EntityType = AuditEntityTypes.TemplateVersion,
                EntityId = $"{templateKey.Value!.Value}:{version.Version}",
                DetailsJson = PublicationDetails(version, report, current?.Version),
                OccurredAt = now,
            }));

            // Forcing the status write makes the update carry the template's
            // concurrency token: a deprecate/disable committed after the load
            // above turns this publication into a concurrency conflict.
            dbContext.Entry(template).Property(entity => entity.Status).IsModified = true;

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
                    "The version changed while the publication was in flight. Validate and publish again."));
            }
            catch (DbUpdateException exception)
                when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                return Result.BusinessRuleViolation<Outcome>(DomainError.Format(
                    ErrorCodes.PublicationConflict,
                    "Another publication for this template landed concurrently. "
                    + "Fetch the current state and retry if still applicable."));
            }

            logger.VersionPublished(version.TemplateKey.Value, version.Version, current?.Version);
            return Result.Success<Outcome>(new Outcome.Published(Response.From(version, current?.Version)));
        }

        private static string PublicationDetails(TemplateVersion version, ValidationReport report, int? supersededVersion)
            => JsonSerializer.Serialize(new
            {
                contentHash = version.ContentHash,
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
