using Microsoft.Extensions.Options;
using NotificationHub.Api.Infrastructure.Messaging;
using NotificationHub.Api.Infrastructure.Messaging.Consuming;
using NotificationHub.Api.Modules.Audit.Integration.V1;
using NotificationHub.Api.Modules.ContactConsent.Infrastructure.Consuming;
using NotificationHub.Api.Modules.ContactConsent.Infrastructure.Persistence;
using NotificationHub.Api.Modules.ContactConsent.Integration.V1;

namespace NotificationHub.Api.Modules.ContactConsent.Features.Ingress;

/// <summary>
/// Consumes the contact and consent declarations of the registration system.
/// It owns the transport and nothing else: read the CloudEvents envelope,
/// answer whether the source is one this role accepts, hand the body to the
/// use case that owns the declaration, and turn the outcome into a settlement
/// on the log.
///
/// Authorization runs before anything reads the body. The list of accepted
/// sources is the actor vocabulary of this transport, so a record from outside
/// it has no identity to write the consent ledger with and no reason to have
/// its payload inspected.
///
/// Deduplication is a single layer, and the mark lives inside the transaction
/// of the effect. There is no unique business key behind it: a declaration is
/// desired state over an append-only ledger, so the handlers already answer a
/// repeated declaration with zero writes. What the mark protects is the trail:
/// the no-op path appends an audit entry, and without a mark a rebalance would
/// fill the hash-chained trail with entries of an event already settled.
/// </summary>
internal sealed class ContactsIngressProcessor(
    ContactDeclarationApplier applier,
    ContactConsentWriter writer,
    ContactIngestionDeadLetterWriter deadLetterWriter,
    IOptions<ContactsIngressOptions> options,
    ILogger<ContactsIngressProcessor> logger) : IKafkaMessageProcessor
{
    /// <summary>Consumer name recorded with every deduplication mark of this role.</summary>
    internal const string ConsumerName = "contacts-ingress";

    private const int MaxRecipientIdLength = 100;

    public string Consumer => ConsumerName;

    public async Task<KafkaDisposition> ProcessAsync(
        KafkaMessageContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var provenance = new ContactWriteProvenance
        {
            RecordId = context.DedupeId,
            Consumer = ConsumerName,
            EventId = context.Event?.Id,
        };

        if (context.Event is not { } cloudEvent)
        {
            return await RefuseAsync(
                context,
                provenance,
                new ContactIngestionDiagnosis
                {
                    Reason = ContactIngestionRejectionReasons.PayloadInvalid,
                },
                cancellationToken);
        }

        if (!IsAccepted(cloudEvent.Source))
        {
            // The body of an unaccepted source is not read at all, not even to
            // summarize it: the record is evidence of a refusal, and this hub
            // has no reason to inspect data it will not apply.
            return await RefuseAsync(
                context,
                provenance,
                Diagnose(cloudEvent, ContactIngestionRejectionReasons.SourceNotAuthorized, withBody: false),
                cancellationToken);
        }

        if (cloudEvent.Subject is not { Length: > 0 } recipientId
            || recipientId.Length > MaxRecipientIdLength)
        {
            return await RefuseAsync(
                context,
                provenance,
                Diagnose(cloudEvent, ContactIngestionRejectionReasons.PayloadInvalid),
                cancellationToken);
        }

        // The accepted source is the identity of this transport, the way the
        // token's application id is the identity of the REST one.
        var writeContext = new ContactWriteContext(cloudEvent.Source, AuditActorTypes.System, provenance);
        ContactIngestionResult result = await applier.ApplyAsync(
            cloudEvent.Type, recipientId, cloudEvent.Data, writeContext, cancellationToken);

        switch (result)
        {
            case ContactIngestionResult.Applied:
                logger.DeclarationApplied(
                    context.Topic, context.Partition, context.Offset, cloudEvent.Type, recipientId);
                return new KafkaDisposition.Processed();

            case ContactIngestionResult.Duplicate:
                logger.DeclarationRedelivered(context.Topic, context.Partition, context.Offset);
                return new KafkaDisposition.Duplicate();

            case ContactIngestionResult.Conflict:
                logger.DeclarationConflicted(
                    context.Topic, context.Partition, context.Offset, recipientId);
                return new KafkaDisposition.Retry("conflito de escrita concorrente na declaração");

            case ContactIngestionResult.Refused refused:
                return await RefuseAsync(
                    context, provenance, Diagnose(cloudEvent, refused.Reason), cancellationToken);

            default:
                throw new InvalidOperationException(
                    $"Desfecho de ingestão não suportado: {result.GetType().Name}.");
        }
    }

    /// <summary>
    /// Records the refusal on the dead-letter topic and only then commits the
    /// deduplication mark. The order is the whole point: a mark written first
    /// would make the replay of a crash skip a record nobody ever put on the
    /// dead-letter topic.
    /// </summary>
    private async Task<KafkaDisposition> RefuseAsync(
        KafkaMessageContext context,
        ContactWriteProvenance provenance,
        ContactIngestionDiagnosis diagnosis,
        CancellationToken cancellationToken)
    {
        await deadLetterWriter.ProduceAsync(context, diagnosis, cancellationToken);
        var marked = await writer.TryMarkProcessedAsync(provenance, cancellationToken);
        return marked
            ? new KafkaDisposition.DeadLetter(diagnosis.Reason)
            : new KafkaDisposition.Duplicate();
    }

    private bool IsAccepted(string source)
        => options.Value.AcceptedSources.Contains(source, StringComparer.Ordinal);

    private static ContactIngestionDiagnosis Diagnose(
        CloudEvent cloudEvent,
        string reason,
        bool withBody = true)
        => new()
        {
            Reason = reason,
            EventType = cloudEvent.Type,
            EventSource = cloudEvent.Source,
            EventId = cloudEvent.Id,
            Data = withBody ? cloudEvent.Data : null,
        };
}
