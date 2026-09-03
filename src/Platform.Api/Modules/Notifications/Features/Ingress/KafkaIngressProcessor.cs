using System.Text.Json;
using FluentValidation;
using FluentValidation.Results;
using NotificationHub.Api.Infrastructure.Messaging;
using NotificationHub.Api.Infrastructure.Messaging.Consuming;
using NotificationHub.Api.Modules.Notifications.Domain;
using NotificationHub.Api.Modules.Notifications.Features.KillSwitch;
using RequestNotificationUseCase = NotificationHub.Api.Modules.Notifications.Features.Ingress.RequestNotification.RequestNotification;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Authorization;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Consuming;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Http;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Persistence;
using NotificationHub.Api.Modules.Notifications.Integration.V1;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.Notifications.Features.Ingress;

/// <summary>
/// Consumes producer requests from the corporate bus. It owns no ingestion
/// rule: every decision comes from the same use case the REST route calls,
/// with the same validator, the same catalog gate, the same idempotency
/// contract and the same recipient budget. What belongs to this transport
/// lives here and nowhere else: reading the CloudEvents envelope, answering
/// the authorization question against the producer registry, and turning a
/// refusal into a dead-letter record instead of an HTTP problem.
///
/// The order of the checks is the contract. The envelope type is checked
/// before the body binds, because the type is the schema version and binding
/// first would let a later version pass on the coincidence of field names.
/// Authorization runs before the
/// catalog, so a principal outside the registry never learns which templates
/// exist from the difference between two refusal reasons. The
/// sensitive-variable restriction runs before the schema validation, because
/// the validation reports findings over exactly the payload that must not be
/// inspected. Idempotency runs before the recipient budget, so a legitimate
/// replay never spends it.
/// </summary>
internal sealed class KafkaIngressProcessor(
    RequestNotificationUseCase.Handler handler,
    IValidator<RequestNotificationUseCase.Command> validator,
    KafkaProducerAuthorizer authorizer,
    KafkaIngressSettlement settlement,
    KafkaIngressTopicMap topicMap,
    IKillSwitch killSwitch,
    ILogger<KafkaIngressProcessor> logger) : IKafkaMessageProcessor
{
    internal const string KillSwitchUnavailableReason = "producer-kill-switch-unavailable";

    /// <summary>
    /// The only envelope type this consumer accepts. The type is the schema
    /// version, and it is checked before anything binds the body: a later
    /// version whose fields happen to carry the same names would bind by luck
    /// and be processed as if the producer had spoken this contract.
    /// </summary>
    internal const string RequestedEventType = "araia.notification.requested.v1";

    public string Consumer => IngressCommitWriter.ConsumerName;

    public async Task<KafkaDisposition> ProcessAsync(
        KafkaMessageContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var producer = topicMap.ResolveLogicalProducer(context.Topic);
        if (context.Event is not { } cloudEvent)
        {
            return await settlement.RefuseAsync(
                context,
                new DeadLetterDiagnosis
                {
                    Reason = NotificationRejectionReasons.PayloadInvalid,
                    Producer = producer,
                },
                cancellationToken);
        }

        if (!string.Equals(cloudEvent.Type, RequestedEventType, StringComparison.Ordinal))
        {
            return await settlement.RefuseAsync(
                context,
                new DeadLetterDiagnosis
                {
                    Reason = NotificationRejectionReasons.EventTypeUnsupported,
                    Producer = producer,
                },
                cancellationToken);
        }

        if (IngressRequestBinder.Bind(cloudEvent.Data) is not { } request)
        {
            return await settlement.RefuseAsync(
                context,
                new DeadLetterDiagnosis
                {
                    Reason = NotificationRejectionReasons.PayloadInvalid,
                    Producer = producer,
                },
                cancellationToken);
        }

        // Use the handler's validator before consulting either authority. The
        // handler repeats the same validation when it records the outcome, so
        // the transport does not duplicate any rule or refusal construction.
        ValidationResult validation = await validator.ValidateAsync(request.Command, cancellationToken);
        if (!validation.IsValid)
        {
            return await HandleAsync(
                context,
                cloudEvent,
                request,
                producer,
                new ProducerAuthorization.Allowed(),
                cancellationToken);
        }

        KillSwitchEvaluation evaluation = await killSwitch.EvaluateAsync(
            KillSwitchScope.Producer,
            producer,
            cancellationToken);
        ProducerAuthorization authorization;
        switch (evaluation)
        {
            case KillSwitchEvaluation.Allowed:
                authorization = await authorizer.AuthorizeAsync(
                    producer,
                    request.Command.Application,
                    request.Command.Class,
                    cancellationToken);
                break;
            case KillSwitchEvaluation.Blocked:
                authorization = new ProducerAuthorization.Denied(
                    NotificationRejectionReasons.ProducerDisabled);
                break;
            case KillSwitchEvaluation.Unavailable:
                return new KafkaDisposition.Retry(KillSwitchUnavailableReason);
            default:
                throw new InvalidOperationException(
                    $"Avaliação de kill switch desconhecida: {evaluation}.");
        }

        return await HandleAsync(
            context, cloudEvent, request, producer, authorization, cancellationToken);
    }

    private async Task<KafkaDisposition> HandleAsync(
        KafkaMessageContext context,
        CloudEvent cloudEvent,
        IngressRequest request,
        string producer,
        ProducerAuthorization authorization,
        CancellationToken cancellationToken)
    {
        Result<RequestNotificationUseCase.Outcome> result = await handler.HandleAsync(
            request.Command,
            producer,
            authorization,
            OriginOf(context, cloudEvent),
            request.IdempotencyKey,
            cancellationToken);
        if (result.IsFailure)
        {
            throw new InvalidOperationException(
                $"A ingestão pelo barramento falhou de forma inesperada: {result.Error}");
        }

        return await SettleAsync(context, request, producer, result.Value!, cancellationToken);
    }

    private async Task<KafkaDisposition> SettleAsync(
        KafkaMessageContext context,
        IngressRequest request,
        string producer,
        RequestNotificationUseCase.Outcome outcome,
        CancellationToken cancellationToken)
    {
        switch (outcome)
        {
            case RequestNotificationUseCase.Outcome.Accepted accepted:
                logger.IngressEventAccepted(context.Topic, context.Partition, context.Offset, accepted.NotificationId);
                return await settlement.CommitAsync(
                    context, new KafkaDisposition.Processed(), cancellationToken);

            case RequestNotificationUseCase.Outcome.Replayed replayed:
                logger.IngressEventReplayed(context.Topic, context.Partition, context.Offset, replayed.NotificationId);
                return await settlement.CommitAsync(
                    context, new KafkaDisposition.Duplicate(), cancellationToken);

            case RequestNotificationUseCase.Outcome.SensitiveVariablesOnBus sensitive:
                return await settlement.RefuseAsync(
                    context,
                    Diagnose(request, producer, NotificationRejectionReasons.SensitiveVariablesOnBus)
                        with { RedactedVariableNames = sensitive.VariableNames },
                    cancellationToken);

            case RequestNotificationUseCase.Outcome.ProducerNotAuthorized denied:
                return await settlement.RefuseAsync(
                    context, Diagnose(request, producer, denied.Reason), cancellationToken);

            case RequestNotificationUseCase.Outcome.TemplateRejected rejected:
                return await settlement.RefuseAsync(
                    context, Diagnose(request, producer, rejected.Reason), cancellationToken);

            case RequestNotificationUseCase.Outcome.PayloadInvalid:
                return await settlement.RefuseAsync(
                    context,
                    Diagnose(request, producer, NotificationRejectionReasons.PayloadInvalid),
                    cancellationToken);

            case RequestNotificationUseCase.Outcome.IdempotencyConflict:
                return await settlement.RefuseAsync(
                    context,
                    Diagnose(request, producer, NotificationRejectionReasons.IdempotencyKeyConflict),
                    cancellationToken);

            // Permanently invalid on this transport: the set the event names
            // will not become claimable by redelivering the same event, so the
            // record goes to the dead-letter topic with the reason the
            // synchronous surface answers with.
            case RequestNotificationUseCase.Outcome.AttachmentsNotClaimable:
                return await settlement.RefuseAsync(
                    context,
                    Diagnose(request, producer, IngestionProblems.AttachmentsNotClaimableType),
                    cancellationToken);

            case RequestNotificationUseCase.Outcome.RateLimited:
                // Only the recipient budget rejects on this path: the
                // principal dimension is counted and observed, never refused.
                return await settlement.RefuseAsync(
                    context,
                    Diagnose(request, producer, NotificationRejectionReasons.RecipientRateLimited),
                    cancellationToken);

            default:
                throw new InvalidOperationException(
                    $"Desfecho de ingestão não suportado: {outcome.GetType().Name}.");
        }
    }

    /// <summary>
    /// The provenance of one bus request as the trail must record it. The
    /// coordinates travel opaque: the use case writes them and never reads
    /// them, and they are what lets a disputed request be checked against the
    /// record the broker still holds.
    /// </summary>
    private static RequestNotificationUseCase.IngestionOrigin OriginOf(
        KafkaMessageContext context,
        CloudEvent cloudEvent)
        => new()
        {
            Source = RequestNotificationUseCase.IngestionSource.Kafka,
            Topic = context.Topic,
            Partition = context.Partition,
            Offset = context.Offset,
            EventId = cloudEvent.Id,
        };

    private static DeadLetterDiagnosis Diagnose(IngressRequest request, string producer, string reason)
        => new()
        {
            Reason = reason,
            Producer = producer,
            Application = request.Command.Application,
            Class = request.Command.Class,
            IdempotencyKey = request.IdempotencyKey,
        };

}

/// <summary>
/// Settles one ingress record after its outcome is known. A refusal reaches
/// the dead-letter topic before its trail and deduplication mark commit, so an
/// offset can advance only after both durable records exist.
/// </summary>
internal sealed class KafkaIngressSettlement(
    DeferredTrailIngestionSink sink,
    IngressCommitWriter commitWriter,
    IngressDeadLetterWriter deadLetterWriter)
{
    internal async Task<KafkaDisposition> RefuseAsync(
        KafkaMessageContext context,
        DeadLetterDiagnosis diagnosis,
        CancellationToken cancellationToken)
    {
        await deadLetterWriter.ProduceAsync(context, diagnosis, cancellationToken);
        return await CommitAsync(
            context,
            new KafkaDisposition.DeadLetter(diagnosis.Reason),
            cancellationToken);
    }

    internal async Task<KafkaDisposition> CommitAsync(
        KafkaMessageContext context,
        KafkaDisposition disposition,
        CancellationToken cancellationToken)
    {
        var committed = await commitWriter.TryCommitAsync(context.DedupeId, sink, cancellationToken);
        return committed ? disposition : new KafkaDisposition.Duplicate();
    }
}

/// <summary>One bus request bound to the ingestion command, with its idempotency scope.</summary>
internal sealed record IngressRequest(RequestNotificationUseCase.Command Command, string IdempotencyKey);

/// <summary>
/// Binds the event body to the ingestion command. Required command fields stay
/// permissive so the shared validator can report their value rules. Optional
/// fields preserve missing and JSON null as absence, but reject a value whose
/// JSON type or format cannot represent the command contract. The idempotency
/// key is transport identity and must be valid before any persistence starts.
///
/// Every member the request contract names is read here, and the manifest of
/// attachment references is one of them. A member this binder did not read
/// would be dropped in silence: the body would still bind, the request would
/// still be accepted, and the producer would receive the acceptance of a
/// notification it never asked for. That failure keeps the syntax valid and
/// changes the effect, so a member of the contract is transported or the body
/// that names it is refused, and never bound without it.
///
/// A manifest that arrives empty is bound empty rather than as absence. An
/// empty list is a producer asking for attachments without naming one, which
/// is a different request from one that never asked, and turning it into
/// absence here would answer it with an acceptance instead of the refusal the
/// shared validator owes it.
/// </summary>
internal static class IngressRequestBinder
{
    private const int MaxIdempotencyKeyLength = 200;

    public static IngressRequest? Bind(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        // Ahead of every field, because reading one is what transcodes it and
        // the transcoding throws on an escape that names no character. Thrown
        // here the failure is deterministic but reaches the transport as an
        // ordinary one, which retries it and then stops the partition; the
        // refusal a malformed body already takes is what belongs here.
        //
        // The refusal covers the whole body rather than the fields read below:
        // a lookup unescapes candidate keys to compare them, so which read
        // reaches an unreadable escape first depends on the names sought and
        // on their length, and a guard shaped by that reopens the moment a
        // field is added or renamed. The payload fields are refused here too,
        // and the shared validator still refuses them on its own, which is
        // what closes the same fault on the synchronous route.
        if (!CompactJsonSize.Measure(data).IsReadable)
        {
            return null;
        }

        var idempotencyKey = ReadString(data, "idempotencyKey");
        if (string.IsNullOrWhiteSpace(idempotencyKey)
            || idempotencyKey.Length > MaxIdempotencyKeyLength)
        {
            return null;
        }

        if (!TryReadOptionalString(data, "locale", out var locale)
            || !TryReadOptionalObject(data, "variables", out JsonElement? variables)
            || !TryReadOptionalObject(data, "metadata", out JsonElement? metadata)
            || !TryReadOptionalStringArray(
                data,
                "channelsHint",
                out IReadOnlyList<string>? channelsHint)
            || !TryReadOptionalStringArray(
                data,
                "attachments",
                out IReadOnlyList<string>? attachments)
            || !TryReadOptionalString(data, "correlationId", out var correlationId)
            || !TryReadOptionalDateTimeOffset(data, "scheduledAt", out DateTimeOffset? scheduledAt))
        {
            return null;
        }

        var command = new RequestNotificationUseCase.Command(
            ReadString(data, "application") ?? string.Empty,
            ReadString(data, "recipientId") ?? string.Empty,
            ReadString(data, "class") ?? string.Empty,
            ReadString(data, "templateKey") ?? string.Empty,
            ReadInt32(data, "ttlSeconds"))
        {
            Locale = locale,
            Variables = variables,
            Metadata = metadata,
            ChannelsHint = channelsHint,
            CorrelationId = correlationId,
            ScheduledAt = scheduledAt,
            Attachments = attachments,
        };
        return new IngressRequest(command, idempotencyKey);
    }

    private static string? ReadString(JsonElement data, string name)
        => data.TryGetProperty(name, out JsonElement element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;

    private static int ReadInt32(JsonElement data, string name)
        => data.TryGetProperty(name, out JsonElement element)
            && element.ValueKind == JsonValueKind.Number
            && element.TryGetInt32(out var value)
                ? value
                : 0;

    private static bool TryReadOptionalString(
        JsonElement data,
        string name,
        out string? value)
    {
        if (!data.TryGetProperty(name, out JsonElement element)
            || element.ValueKind == JsonValueKind.Null)
        {
            value = null;
            return true;
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            value = null;
            return false;
        }

        value = element.GetString();
        return true;
    }

    private static bool TryReadOptionalObject(
        JsonElement data,
        string name,
        out JsonElement? value)
    {
        if (!data.TryGetProperty(name, out JsonElement element)
            || element.ValueKind == JsonValueKind.Null)
        {
            value = null;
            return true;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            value = null;
            return false;
        }

        value = element.Clone();
        return true;
    }

    private static bool TryReadOptionalStringArray(
        JsonElement data,
        string name,
        out IReadOnlyList<string>? value)
    {
        if (!data.TryGetProperty(name, out JsonElement element)
            || element.ValueKind == JsonValueKind.Null)
        {
            value = null;
            return true;
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            value = null;
            return false;
        }

        List<string> items = [];
        foreach (JsonElement item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                value = null;
                return false;
            }

            items.Add(item.GetString()!);
        }

        value = items;
        return true;
    }

    private static bool TryReadOptionalDateTimeOffset(
        JsonElement data,
        string name,
        out DateTimeOffset? value)
    {
        if (!data.TryGetProperty(name, out JsonElement element)
            || element.ValueKind == JsonValueKind.Null)
        {
            value = null;
            return true;
        }

        if (element.ValueKind != JsonValueKind.String
            || !element.TryGetDateTimeOffset(out DateTimeOffset parsed))
        {
            value = null;
            return false;
        }

        value = parsed;
        return true;
    }
}
