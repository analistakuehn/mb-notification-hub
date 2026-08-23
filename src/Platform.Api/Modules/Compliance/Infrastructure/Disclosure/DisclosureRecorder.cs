using System.Text.Json;
using NotificationHub.Api.Modules.Audit.Integration.V1;

namespace NotificationHub.Api.Modules.Compliance.Infrastructure.Disclosure;

/// <summary>
/// Builds and appends the trail record of one disclosure. It runs after the
/// evidence is composed and before the first byte of the answer is written, in a
/// transaction of its own, and a failure here refuses the answer.
/// </summary>
/// <remarks>
/// What the details may carry is the whole point of this class living apart from
/// the handlers: the route, the disclosed scope, the access identifier, the
/// attempt sequences and the disclosed hashes. Never a contact value, never a
/// fragment of content, never a variable. A trail that quoted what it protects
/// would be the leak it was built to detect.
///
/// Every link of one call shares one access identifier, so counting distinct
/// accesses stays independent of how many subjects a scope happens to touch.
/// </remarks>
internal sealed class DisclosureRecorder(IAuditDisclosureTrail trail, TimeProvider timeProvider)
{
    public async Task RecordAsync(EvidenceDisclosure disclosure, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(disclosure);
        DateTimeOffset now = timeProvider.GetUtcNow();

        // One call is one access, whatever the number of subjects it touched.
        // Without a shared identifier an auditor counting rows would read two
        // accesses where there was one, and "who looked at this afterwards"
        // would inflate with every subject a future answer happens to reach.
        var accessId = Guid.CreateVersion7().ToString("D");
        var details = JsonSerializer.Serialize(new
        {
            route = disclosure.Actor.Route,
            scope = DisclosureScopes.NotificationEvidence,
            accessId,
            recipientId = disclosure.RecipientId,
            attempts = disclosure.Attempts.Select(attempt => new
            {
                sequence = attempt.Sequence,
                contentHashMasked = attempt.ContentHashMasked,
                contentHashFull = attempt.ContentHashFull,
            }),
            trailLinks = disclosure.TrailLinkCount,
            priorAccesses = disclosure.PriorAccessCount,
        });

        // Two subjects, one transaction: the answer discloses the notification
        // and the recipient's contact history, and each of them earns a link of
        // its own so both stay answerable by subject instead of by scan.
        await trail.RecordAsync(
            [
                Entry(disclosure.Actor, disclosure.Application, AuditEntityTypes.Notification,
                    disclosure.NotificationId.ToString(), details, now),
                Entry(disclosure.Actor, disclosure.Application, AuditEntityTypes.Recipient,
                    disclosure.RecipientId, details, now),
            ],
            cancellationToken);
    }

    public async Task RecordAsync(ContentDisclosure disclosure, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(disclosure);
        DateTimeOffset now = timeProvider.GetUtcNow();

        // One subject here, and the identifier travels all the same: counting
        // distinct accesses must not depend on knowing how many subjects each
        // scope happens to touch.
        var accessId = Guid.CreateVersion7().ToString("D");
        var details = JsonSerializer.Serialize(new
        {
            route = disclosure.Actor.Route,
            scope = DisclosureScopes.AttemptContent,
            accessId,
            attemptSequence = disclosure.Sequence,
            disclosedForm = disclosure.DisclosedForm,
            contentHashMasked = disclosure.ContentHashMasked,
            contentHashFull = disclosure.ContentHashFull,
            contentHashVerified = disclosure.ContentHashVerified,
        });

        await trail.RecordAsync(
            [
                Entry(disclosure.Actor, disclosure.Application, AuditEntityTypes.Notification,
                    disclosure.NotificationId.ToString(), details, now),
            ],
            cancellationToken);
    }

    private static AuditEntry Entry(
        DisclosureActor actor,
        string application,
        string entityType,
        string entityId,
        string details,
        DateTimeOffset occurredAt)
        => new()
        {
            // The audit role is granted to people; a tool holding it is recorded
            // by whichever identity its token carries, never by a fabricated one.
            ActorType = AuditActorTypes.User,
            ActorId = actor.ActorId,
            Application = application,
            Action = AuditActions.AuditRead,
            EntityType = entityType,
            EntityId = entityId,
            DetailsJson = details,
            OccurredAt = occurredAt,
        };
}
