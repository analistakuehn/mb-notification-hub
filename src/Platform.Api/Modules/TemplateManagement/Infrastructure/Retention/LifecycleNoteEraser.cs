using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using NotificationHub.Api.Modules.Audit.Integration.V1;
using NotificationHub.Api.Modules.TemplateManagement.Domain;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Persistence;

namespace NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Retention;

/// <summary>What one erasure round found under the reference it was given.</summary>
internal enum LifecycleNoteErasure
{
    /// <summary>The prose was removed and the removal was recorded.</summary>
    Erased,

    /// <summary>The reference holds no prose; nothing was removed and nothing was recorded.</summary>
    AlreadyAbsent,
}

/// <summary>
/// Removes the prose of one lifecycle note and records that it was removed.
/// <para>
/// The two halves are one act and share one transaction, in the shape the
/// trail contract prescribes: the delete rides the caller's own SaveChanges,
/// the append follows it, and the commit follows the append with nothing in
/// between, because the append holds the chain lock of the partition until the
/// transaction ends.
/// </para>
/// <para>
/// The recorded event carries the same reference the transition recorded, and
/// that is the whole point of it. Without the event, a reader following the
/// reference of an old transition finds nothing, and "no note was ever
/// written" reads exactly like "a note was written and then removed". A record
/// store that can lose a row without saying so is not one an auditor can
/// reason about, and the erasure is the only writer that ever removes anything
/// from this context.
/// </para>
/// <para>
/// There is no HTTP surface here, deliberately. No context of this system
/// exposes a forgetting endpoint, and inventing the first one alongside a
/// storage change would ship an unreviewed capability under cover of a fix.
/// The trigger belongs to a slice of its own.
/// </para>
/// </summary>
internal sealed class LifecycleNoteEraser(
    TemplateManagementDbContext dbContext,
    IAuditTrail auditTrail,
    TimeProvider timeProvider,
    ILogger<LifecycleNoteEraser> logger)
{
    public async Task<LifecycleNoteErasure> EraseAsync(
        Guid noteRef,
        string actor,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);

        LifecycleNote? note = await dbContext.LifecycleNotes
            .FirstOrDefaultAsync(candidate => candidate.Id == noteRef, cancellationToken);
        if (note is null)
        {
            // A replay of the same request, or a reference that never held
            // prose. Recording an erasure here would append a claim that
            // something was removed when nothing was, into a store that
            // refuses correction.
            logger.LifecycleNoteAlreadyAbsent(noteRef);
            return LifecycleNoteErasure.AlreadyAbsent;
        }

        var entry = new AuditEntry
        {
            ActorType = AuditActorTypes.User,
            ActorId = actor,
            Application = note.Application,
            Action = ErasureAction(note.SubjectType),
            EntityType = note.SubjectType,
            EntityId = note.SubjectKey,

            // The reference and nothing else. Neither the prose that is being
            // removed nor a digest of it: a digest of a short value is a
            // lookup table away from the value, and it would survive in the
            // one place that cannot be rewritten.
            DetailsJson = JsonSerializer.Serialize(new { noteRef = note.Id }),
            OccurredAt = timeProvider.GetUtcNow(),
        };

        dbContext.LifecycleNotes.Remove(note);
        await using IDbContextTransaction transaction =
            await dbContext.Database.BeginTransactionAsync(cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditTrail.AppendAsync(transaction.GetDbTransaction(), entry, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        logger.LifecycleNoteErased(noteRef);
        return LifecycleNoteErasure.Erased;
    }

    private static string ErasureAction(string subjectType) => subjectType switch
    {
        AuditEntityTypes.Template => AuditActions.TemplateLifecycleNoteErased,
        AuditEntityTypes.Layout => AuditActions.LayoutLifecycleNoteErased,
        _ => throw new InvalidOperationException(
            $"Nota de ciclo de vida com sujeito desconhecido: '{subjectType}'."),
    };
}
