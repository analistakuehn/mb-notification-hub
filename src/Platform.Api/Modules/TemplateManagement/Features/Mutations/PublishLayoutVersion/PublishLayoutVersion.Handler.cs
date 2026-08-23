using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NotificationHub.Api.Modules.Audit.Integration.V1;
using NotificationHub.Api.Modules.TemplateManagement.Domain;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.ErrorHandling;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Persistence;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Templating;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.TemplateManagement.Features.Mutations;

internal static partial class PublishLayoutVersion
{
    internal sealed class Handler(
        TemplateManagementDbContext dbContext,
        IAuditTrail auditTrail,
        LayoutVersionAnalyzer analyzer,
        TimeProvider timeProvider,
        ILogger<Handler> logger)
    {
        public async Task<Result<Outcome>> HandleAsync(
            string key,
            int versionNumber,
            string publisher,
            CancellationToken cancellationToken)
        {
            Result<LayoutKey> layoutKey = LayoutKey.Create(key);
            if (layoutKey.IsFailure)
            {
                return layoutKey.AsFailure<LayoutKey, Outcome>();
            }

            // Tracked on purpose: the publication touches the layout row in
            // the same transaction, so a concurrent lifecycle transition
            // invalidates this publication instead of slipping past the
            // status check below.
            Layout? layout = await dbContext.Layouts
                .WhereKey(layoutKey.Value!)
                .FirstOrDefaultAsync(cancellationToken);
            if (layout is null)
            {
                return Result.NotFound<Outcome>(DomainError.Format(
                    ErrorCodes.LayoutNotFound,
                    $"Layout '{layoutKey.Value!.Value}' does not exist."));
            }

            Result accepting = layout.EnsureAcceptsPublication();
            if (accepting.IsFailure)
            {
                return accepting.AsFailure<Outcome>();
            }

            LayoutVersion? version = await dbContext.LayoutVersions
                .WhereLayoutKey(layoutKey.Value!)
                .FirstOrDefaultAsync(candidate => candidate.Version == versionNumber, cancellationToken);
            if (version is null)
            {
                return Result.NotFound<Outcome>(DomainError.Format(
                    ErrorCodes.LayoutVersionNotFound,
                    $"Layout '{layoutKey.Value!.Value}' has no version {versionNumber}."));
            }

            Result eligibility = version.CanBePublishedBy(publisher);
            if (eligibility.IsFailure)
            {
                return eligibility.AsFailure<Outcome>();
            }

            // Same validation catalog the authoring endpoints expose: a version
            // only publishes after passing it again, in full, right now.
            ValidationReport report = LayoutValidation.Validate(version, analyzer.Analyze(version));
            if (!report.Passed)
            {
                var failed = report.Checks.Count(check => check.Status == ValidationCheckStatuses.Failed);
                logger.LayoutPublicationBlocked(version.LayoutKey.Value, version.Version, failed);
                return Result.Success<Outcome>(new Outcome.Blocked(report));
            }

            Result integrity = version.VerifyContentHash();
            if (integrity.IsFailure)
            {
                return integrity.AsFailure<Outcome>();
            }

            LayoutVersion? current = await dbContext.LayoutVersions
                .WhereLayoutKey(layoutKey.Value!)
                .Where(candidate => candidate.Status == LayoutVersionStatus.Published)
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

            var grant = new ApprovalGrant
            {
                SubjectType = ApprovalSubjectTypes.LayoutVersion,
                SubjectId = layoutKey.Value!.Value,
                SubjectVersion = version.Version,
                ContentHash = version.ContentHash,
                Role = ApprovalRoles.Publisher,
                ApproverOid = publisher,
                ApprovedAt = now,
            };
            var entry = new AuditEntry
            {
                ActorType = AuditActorTypes.User,
                ActorId = publisher,
                Action = AuditActions.LayoutVersionPublished,
                EntityType = AuditEntityTypes.LayoutVersion,
                EntityId = $"{layoutKey.Value!.Value}:{version.Version}",
                DetailsJson = PublicationDetails(version, report, current?.Version),
                OccurredAt = now,
            };

            // Forcing the status write makes the update carry the layout's
            // concurrency token: a deprecate/disable committed after the load
            // above turns this publication into a concurrency conflict.
            dbContext.Entry(layout).Property(entity => entity.Status).IsModified = true;

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
                    "The version changed while the publication was in flight. Validate and publish again."));
            }
            catch (DbUpdateException exception)
                when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                return Result.BusinessRuleViolation<Outcome>(DomainError.Format(
                    ErrorCodes.PublicationConflict,
                    "Another publication for this layout landed concurrently. "
                    + "Fetch the current state and retry if still applicable."));
            }

            logger.LayoutVersionPublished(version.LayoutKey.Value, version.Version, current?.Version);
            return Result.Success<Outcome>(new Outcome.Published(Response.From(version, current?.Version)));
        }

        private static string PublicationDetails(LayoutVersion version, ValidationReport report, int? supersededVersion)
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
