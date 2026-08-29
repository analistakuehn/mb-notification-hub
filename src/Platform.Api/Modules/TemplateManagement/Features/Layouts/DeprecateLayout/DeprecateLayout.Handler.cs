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

internal static partial class DeprecateLayout
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

            Result transition = layout.Deprecate();
            if (transition.IsFailure)
            {
                return transition.AsFailure<Response>();
            }

            // The note is minted before the entry that references it and is
            // written by the SaveChanges that already runs below, one
            // statement before the append. Nothing is added between the append
            // and the commit: the append takes the chain advisory lock of the
            // partition and holds it until the transaction ends, so work
            // placed after it stretches the serialization window of every
            // governed effect of the month.
            DateTimeOffset occurredAt = timeProvider.GetUtcNow();
            LifecycleNote? note = LifecycleNote.For(
                command.Note,
                AuditEntityTypes.Layout,
                key.Value!.Value,
                application: null,
                command.Actor,
                occurredAt);
            if (note is not null)
            {
                dbContext.LifecycleNotes.Add(note);
            }

            var entry = new AuditEntry
            {
                ActorType = AuditActorTypes.User,
                ActorId = command.Actor,
                Action = AuditActions.LayoutDeprecated,
                EntityType = AuditEntityTypes.Layout,
                EntityId = key.Value!.Value,
                DetailsJson = JsonSerializer.Serialize(new { reason = command.Reason, noteRef = note?.Id }),
                OccurredAt = occurredAt,
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
            // persist never drops what the store still answers. A deprecated
            // layout keeps framing what already pins it, so no render outcome
            // moves here; the entry is dropped all the same, because it holds
            // the status of the identity and keeping a status known to be
            // false is what armed the last defect on this surface.
            cache.InvalidatePointer(PublishedPointerKeys.LayoutIdentity(key.Value!.Value));
            logger.LayoutDeprecated(key.Value!.Value);
            return Result.Success(new Response(key.Value!.Value, layout.Status.Canonical()));
        }
    }
}
