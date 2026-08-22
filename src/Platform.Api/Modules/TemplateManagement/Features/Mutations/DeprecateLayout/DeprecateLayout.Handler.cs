using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NotificationHub.Api.Modules.TemplateManagement.Domain;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.ErrorHandling;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Persistence;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.TemplateManagement.Features.Mutations;

internal static partial class DeprecateLayout
{
    internal sealed class Handler(
        TemplateManagementDbContext dbContext,
        TimeProvider timeProvider,
        ILogger<Handler> logger)
    {
        public async Task<Result<Response>> HandleAsync(Command command, CancellationToken cancellationToken)
        {
            Result<LayoutKey> key = LayoutKey.Create(command.Key);
            if (key.IsFailure)
            {
                return key.AsFailure<LayoutKey, Response>();
            }

            Layout? layout = await dbContext.Layouts
                .WhereKey(key.Value!)
                .FirstOrDefaultAsync(cancellationToken);
            if (layout is null)
            {
                return Result.NotFound<Response>(DomainError.Format(
                    ErrorCodes.LayoutNotFound,
                    $"Layout '{key.Value!.Value}' does not exist."));
            }

            Result transition = layout.Deprecate();
            if (transition.IsFailure)
            {
                return transition.AsFailure<Response>();
            }

            dbContext.AuditEvents.Add(AuditEvent.Record(new AuditEntry
            {
                ActorType = AuditActorTypes.User,
                ActorId = command.Actor,
                Action = AuditActions.LayoutDeprecated,
                EntityType = AuditEntityTypes.Layout,
                EntityId = key.Value!.Value,
                DetailsJson = JsonSerializer.Serialize(new { reason = command.Reason }),
                OccurredAt = timeProvider.GetUtcNow(),
            }));

            // One SaveChanges, one transaction: the transition and its audit
            // event land together or not at all.
            await dbContext.SaveChangesAsync(cancellationToken);
            logger.LayoutDeprecated(key.Value!.Value);
            return Result.Success(new Response(key.Value!.Value, layout.Status.Canonical()));
        }
    }
}
