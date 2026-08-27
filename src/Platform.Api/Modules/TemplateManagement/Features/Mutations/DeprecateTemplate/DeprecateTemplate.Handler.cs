using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using NotificationHub.Api.Modules.Audit.Integration.V1;
using NotificationHub.Api.Modules.TemplateManagement.Domain;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.ErrorHandling;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Integration;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Persistence;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.TemplateManagement.Features.Mutations;

internal static partial class DeprecateTemplate
{
    internal sealed class Handler(
        TemplateManagementDbContext dbContext,
        IAuditTrail auditTrail,
        PublishedReadCache cache,
        TimeProvider timeProvider,
        ILogger<Handler> logger)
    {
        public async Task<Result<Response>> HandleAsync(Command command, CancellationToken cancellationToken)
        {
            Result<TemplateKey> key = TemplateKey.Create(command.Key);
            if (key.IsFailure)
            {
                return key.AsFailure<TemplateKey, Response>();
            }

            Template? template = await dbContext.Templates
                .WhereKey(key.Value!)
                .FirstOrDefaultAsync(cancellationToken);
            if (template is null)
            {
                return Result.NotFound<Response>(DomainError.Format(
                    ErrorCodes.TemplateNotFound,
                    $"Template '{key.Value!.Value}' does not exist."));
            }

            Result transition = template.Deprecate();
            if (transition.IsFailure)
            {
                return transition.AsFailure<Response>();
            }

            var entry = new AuditEntry
            {
                ActorType = AuditActorTypes.User,
                ActorId = command.Actor,
                Application = template.Application,
                Action = AuditActions.TemplateDeprecated,
                EntityType = AuditEntityTypes.Template,
                EntityId = key.Value!.Value,
                DetailsJson = JsonSerializer.Serialize(new { reason = command.Reason }),
                OccurredAt = timeProvider.GetUtcNow(),
            };

            // One database transaction shared with the audit contract: the
            // transition and its audit event land together or not at all.
            await using IDbContextTransaction transaction =
                await dbContext.Database.BeginTransactionAsync(cancellationToken);
            try
            {
                await dbContext.SaveChangesAsync(cancellationToken);
                await auditTrail.AppendAsync(transaction.GetDbTransaction(), entry, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                return Result.BusinessRuleViolation<Response>(DomainError.Format(
                    ErrorCodes.PreconditionFailed,
                    "The template changed while the transition was in flight. Fetch the current state and retry."));
            }

            // After the commit and after every exit above: the concurrency
            // path returns without reaching here, so a transition that did not
            // persist never drops what the store still answers. Only this
            // process is reached; every other one keeps answering the previous
            // value until its own pointer expires.
            cache.InvalidatePointer(
                PublishedPointerKeys.Template(template.Application, key.Value!.Value));
            cache.InvalidatePointer(
                PublishedPointerKeys.RenderContext(template.Application, key.Value!.Value));
            logger.TemplateDeprecated(key.Value!.Value);
            return Result.Success(new Response(key.Value!.Value, template.Status.Canonical()));
        }
    }
}
