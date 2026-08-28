using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using NotificationHub.Api.Modules.Audit.Integration.V1;
using NotificationHub.Api.Modules.TemplateManagement.Domain;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.ErrorHandling;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Integration;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Persistence;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.TemplateManagement.Features.Layouts;

internal static partial class DisableLayout
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

            Result transition = layout.Disable();
            if (transition.IsFailure)
            {
                return transition.AsFailure<Response>();
            }

            var entry = new AuditEntry
            {
                ActorType = AuditActorTypes.User,
                ActorId = command.Actor,
                Action = AuditActions.LayoutDisabled,
                EntityType = AuditEntityTypes.Layout,
                EntityId = key.Value!.Value,
                DetailsJson = JsonSerializer.Serialize(new { reason = command.Reason, note = command.Note }),
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
                    "The layout changed while the transition was in flight. Fetch the current state and retry."));
            }

            // After the commit and after every exit above: the concurrency
            // path returns without reaching here, so a transition that did not
            // persist never drops what the store still answers. The identity
            // is the only entry this moves: the versions pinned to this layout
            // stay valid bytes, and the render resolves the identity again
            // before it ever touches one of them.
            cache.InvalidatePointer(PublishedPointerKeys.LayoutIdentity(key.Value!.Value));
            logger.LayoutDisabled(key.Value!.Value);
            return Result.Success(new Response(key.Value!.Value, layout.Status.Canonical()));
        }
    }
}
