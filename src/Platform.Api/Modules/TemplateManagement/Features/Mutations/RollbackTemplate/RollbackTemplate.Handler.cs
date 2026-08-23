using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NotificationHub.Api.Modules.TemplateManagement.Domain;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.ErrorHandling;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Persistence;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Templating;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.TemplateManagement.Features.Mutations;

internal static partial class RollbackTemplate
{
    internal sealed class Handler(
        TemplateManagementDbContext dbContext,
        TemplateVersionAnalyzer analyzer,
        TimeProvider timeProvider,
        ILogger<Handler> logger)
    {
        public async Task<Result<Outcome>> HandleAsync(Command command, CancellationToken cancellationToken)
        {
            Result<TemplateKey> key = TemplateKey.Create(command.Key);
            if (key.IsFailure)
            {
                return key.AsFailure<TemplateKey, Outcome>();
            }

            // Tracked on purpose: the rollback touches the template row in
            // the same transaction, so a concurrent lifecycle transition
            // invalidates this publication instead of slipping past the
            // status check below.
            Template? template = await dbContext.Templates
                .WhereKey(key.Value!)
                .FirstOrDefaultAsync(cancellationToken);
            if (template is null)
            {
                return Result.NotFound<Outcome>(DomainError.Format(
                    ErrorCodes.TemplateNotFound,
                    $"Template '{key.Value!.Value}' does not exist."));
            }

            Result accepting = template.EnsureAcceptsPublication();
            if (accepting.IsFailure)
            {
                return accepting.AsFailure<Outcome>();
            }

            TemplateVersion? source = await dbContext.TemplateVersions
                .AsNoTracking()
                .WhereTemplateKey(key.Value!)
                .FirstOrDefaultAsync(candidate => candidate.Version == command.ToVersion, cancellationToken);
            if (source is null)
            {
                return Result.NotFound<Outcome>(DomainError.Format(
                    ErrorCodes.TemplateVersionNotFound,
                    $"Template '{key.Value!.Value}' has no version {command.ToVersion} to roll back to."));
            }

            // The stored source content must still match the hash its original
            // approval covered before it is cloned into a new publication.
            Result sourceIntegrity = source.VerifyContentHash();
            if (sourceIntegrity.IsFailure)
            {
                return sourceIntegrity.AsFailure<Outcome>();
            }

            var nextVersion = (await dbContext.TemplateVersions
                .AsNoTracking()
                .WhereTemplateKey(key.Value!)
                .MaxAsync(candidate => (int?)candidate.Version, cancellationToken) ?? 0) + 1;

            DateTimeOffset now = timeProvider.GetUtcNow();
            Result<TemplateVersion> rollback = TemplateVersion.CreateRollback(source, nextVersion, command.Actor, now);
            if (rollback.IsFailure)
            {
                return rollback.AsFailure<TemplateVersion, Outcome>();
            }

            TemplateVersion published = rollback.Value!;

            // Same validation catalog as publish: template metadata or the
            // catalog itself may have changed since the source went out.
            LayoutReferenceFacts? layoutReference =
                await dbContext.LoadLayoutReferenceAsync(published, cancellationToken);
            ValidationReport report = TemplateValidation.Validate(
                template, published, analyzer.Analyze(published), layoutReference);
            if (!report.Passed)
            {
                var failed = report.Checks.Count(check => check.Status == ValidationCheckStatuses.Failed);
                logger.RollbackBlocked(key.Value!.Value, source.Version, failed);
                return Result.Success<Outcome>(new Outcome.Blocked(report));
            }

            if (!string.Equals(published.ContentHash, source.ContentHash, StringComparison.Ordinal))
            {
                return Result.BusinessRuleViolation<Outcome>(DomainError.Format(
                    ErrorCodes.ContentHashMismatch,
                    $"The rollback clone of version {source.Version} produced a different content hash."));
            }

            TemplateVersion? current = await dbContext.TemplateVersions
                .WhereTemplateKey(key.Value!)
                .Where(candidate => candidate.Status == TemplateVersionStatus.Published)
                .FirstOrDefaultAsync(cancellationToken);
            if (current is not null)
            {
                Result superseded = current.Supersede();
                if (superseded.IsFailure)
                {
                    return superseded.AsFailure<Outcome>();
                }
            }

            dbContext.TemplateVersions.Add(published);
            dbContext.Approvals.Add(Approval.ForTemplateVersion(
                key.Value!,
                published.Version,
                published.ContentHash,
                command.Actor,
                now));
            dbContext.AuditEvents.Add(AuditEvent.Record(new AuditEntry
            {
                ActorType = AuditActorTypes.User,
                ActorId = command.Actor,
                Application = template.Application,
                Action = AuditActions.TemplateRollback,
                EntityType = AuditEntityTypes.TemplateVersion,
                EntityId = $"{key.Value!.Value}:{published.Version}",
                DetailsJson = RollbackDetails(published, source.Version, current?.Version, report),
                OccurredAt = now,
            }));

            // Forcing the status write makes the update carry the template's
            // concurrency token: a deprecate/disable committed after the load
            // above turns this rollback into a concurrency conflict.
            dbContext.Entry(template).Property(entity => entity.Status).IsModified = true;

            try
            {
                // One SaveChanges, one transaction: the new version, the approval
                // and the audit event land together or not at all.
                await dbContext.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                return Result.BusinessRuleViolation<Outcome>(DomainError.Format(
                    ErrorCodes.PreconditionFailed,
                    "The template changed while the rollback was in flight. Validate and roll back again."));
            }
            catch (DbUpdateException exception)
                when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                return Result.BusinessRuleViolation<Outcome>(DomainError.Format(
                    ErrorCodes.PublicationConflict,
                    "Another publication for this template landed concurrently. "
                    + "Fetch the current state and retry if still applicable."));
            }

            logger.RollbackPublished(key.Value!.Value, published.Version, source.Version);
            return Result.Success<Outcome>(new Outcome.RolledBack(Response.From(published, current?.Version)));
        }

        private static string RollbackDetails(
            TemplateVersion published,
            int fromVersion,
            int? supersededVersion,
            ValidationReport report)
            => JsonSerializer.Serialize(new
            {
                rolledBackFrom = fromVersion,
                publishedVersion = published.Version,
                contentHash = published.ContentHash,
                supersededVersion,
                validation = new { passed = report.Passed },
            });
    }
}
