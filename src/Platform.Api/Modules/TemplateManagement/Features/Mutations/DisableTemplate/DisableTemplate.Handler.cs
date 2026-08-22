using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NotificationHub.Api.Modules.TemplateManagement.Domain;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.ErrorHandling;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Persistence;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.TemplateManagement.Features.Mutations;

internal static partial class DisableTemplate
{
    internal sealed class Handler(
        TemplateManagementDbContext dbContext,
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

            Result transition = template.Disable();
            if (transition.IsFailure)
            {
                return transition.AsFailure<Response>();
            }

            dbContext.AuditEvents.Add(AuditEvent.Record(new AuditEntry
            {
                ActorType = AuditActorTypes.User,
                ActorId = command.Actor,
                Application = template.Application,
                Action = AuditActions.TemplateDisabled,
                EntityType = AuditEntityTypes.Template,
                EntityId = key.Value!.Value,
                DetailsJson = JsonSerializer.Serialize(new { reason = command.Reason }),
                OccurredAt = timeProvider.GetUtcNow(),
            }));

            // One SaveChanges, one transaction: the transition and its audit
            // event land together or not at all.
            await dbContext.SaveChangesAsync(cancellationToken);
            logger.TemplateDisabled(key.Value!.Value);
            return Result.Success(new Response(key.Value!.Value, template.Status.Canonical()));
        }
    }
}
