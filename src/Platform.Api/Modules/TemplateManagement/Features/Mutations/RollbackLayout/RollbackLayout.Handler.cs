using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NotificationHub.Api.Modules.TemplateManagement.Domain;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.ErrorHandling;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Persistence;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Templating;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.TemplateManagement.Features.Mutations;

internal static partial class RollbackLayout
{
    internal sealed class Handler(
        TemplateManagementDbContext dbContext,
        LayoutVersionAnalyzer analyzer,
        TimeProvider timeProvider,
        ILogger<Handler> logger)
    {
        public async Task<Result<Outcome>> HandleAsync(Command command, CancellationToken cancellationToken)
        {
            Result<LayoutKey> key = LayoutKey.Create(command.Key);
            if (key.IsFailure)
            {
                return key.AsFailure<LayoutKey, Outcome>();
            }

            // Tracked on purpose: the rollback touches the layout row in the
            // same transaction, so a concurrent lifecycle transition
            // invalidates this publication instead of slipping past the
            // status check below.
            Layout? layout = await dbContext.Layouts
                .WhereKey(key.Value!)
                .FirstOrDefaultAsync(cancellationToken);
            if (layout is null)
            {
                return Result.NotFound<Outcome>(DomainError.Format(
                    ErrorCodes.LayoutNotFound,
                    $"Layout '{key.Value!.Value}' does not exist."));
            }

            Result accepting = layout.EnsureAcceptsPublication();
            if (accepting.IsFailure)
            {
                return accepting.AsFailure<Outcome>();
            }

            LayoutVersion? source = await dbContext.LayoutVersions
                .AsNoTracking()
                .WhereLayoutKey(key.Value!)
                .FirstOrDefaultAsync(candidate => candidate.Version == command.ToVersion, cancellationToken);
            if (source is null)
            {
                return Result.NotFound<Outcome>(DomainError.Format(
                    ErrorCodes.LayoutVersionNotFound,
                    $"Layout '{key.Value!.Value}' has no version {command.ToVersion} to roll back to."));
            }

            // The stored source content must still match the hash its original
            // approval covered before it is cloned into a new publication.
            Result sourceIntegrity = source.VerifyContentHash();
            if (sourceIntegrity.IsFailure)
            {
                return sourceIntegrity.AsFailure<Outcome>();
            }

            var nextVersion = (await dbContext.LayoutVersions
                .AsNoTracking()
                .WhereLayoutKey(key.Value!)
                .MaxAsync(candidate => (int?)candidate.Version, cancellationToken) ?? 0) + 1;

            DateTimeOffset now = timeProvider.GetUtcNow();
            Result<LayoutVersion> rollback = LayoutVersion.CreateRollback(source, nextVersion, command.Actor, now);
            if (rollback.IsFailure)
            {
                return rollback.AsFailure<LayoutVersion, Outcome>();
            }

            LayoutVersion published = rollback.Value!;

            // Same validation catalog as publish: the catalog itself may have
            // changed since the source went out.
            ValidationReport report = LayoutValidation.Validate(published, analyzer.Analyze(published));
            if (!report.Passed)
            {
                var failed = report.Checks.Count(check => check.Status == ValidationCheckStatuses.Failed);
                logger.LayoutRollbackBlocked(key.Value!.Value, source.Version, failed);
                return Result.Success<Outcome>(new Outcome.Blocked(report));
            }

            if (!string.Equals(published.ContentHash, source.ContentHash, StringComparison.Ordinal))
            {
                return Result.BusinessRuleViolation<Outcome>(DomainError.Format(
                    ErrorCodes.ContentHashMismatch,
                    $"The rollback clone of version {source.Version} produced a different content hash."));
            }

            LayoutVersion? current = await dbContext.LayoutVersions
                .WhereLayoutKey(key.Value!)
                .Where(candidate => candidate.Status == LayoutVersionStatus.Published)
                .FirstOrDefaultAsync(cancellationToken);
            if (current is not null)
            {
                Result superseded = current.Supersede();
                if (superseded.IsFailure)
                {
                    return superseded.AsFailure<Outcome>();
                }
            }

            dbContext.LayoutVersions.Add(published);
            dbContext.Approvals.Add(Approval.ForLayoutVersion(
                key.Value!,
                published.Version,
                published.ContentHash,
                command.Actor,
                now));
            dbContext.AuditEvents.Add(AuditEvent.Record(new AuditEntry
            {
                ActorType = AuditActorTypes.User,
                ActorId = command.Actor,
                Action = AuditActions.LayoutRollback,
                EntityType = AuditEntityTypes.LayoutVersion,
                EntityId = $"{key.Value!.Value}:{published.Version}",
                DetailsJson = RollbackDetails(published, source.Version, current?.Version, report),
                OccurredAt = now,
            }));

            // Forcing the status write makes the update carry the layout's
            // concurrency token: a deprecate/disable committed after the load
            // above turns this rollback into a concurrency conflict.
            dbContext.Entry(layout).Property(entity => entity.Status).IsModified = true;

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
                    "The layout changed while the rollback was in flight. Validate and roll back again."));
            }
            catch (DbUpdateException exception)
                when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                return Result.BusinessRuleViolation<Outcome>(DomainError.Format(
                    ErrorCodes.PublicationConflict,
                    "Another publication for this layout landed concurrently. "
                    + "Fetch the current state and retry if still applicable."));
            }

            logger.LayoutRollbackPublished(key.Value!.Value, published.Version, source.Version);
            return Result.Success<Outcome>(new Outcome.RolledBack(Response.From(published, current?.Version)));
        }

        private static string RollbackDetails(
            LayoutVersion published,
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
