using System.Text.Json;
using NotificationHub.Api.Infrastructure.Messaging;
using NotificationHub.Api.Infrastructure.Messaging.Consuming;
using NotificationHub.Api.Modules.Notifications.Features.Mutations;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Authorization;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Consuming;
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
/// The order of the checks is the contract. Authorization runs before the
/// catalog, so a principal outside the registry never learns which templates
/// exist from the difference between two refusal reasons. The
/// sensitive-variable restriction runs before the schema validation, because
/// the validation reports findings over exactly the payload that must not be
/// inspected. Idempotency runs before the recipient budget, so a legitimate
/// replay never spends it.
/// </summary>
internal sealed class KafkaIngressProcessor(
    RequestNotification.Handler handler,
    KafkaProducerAuthorizer authorizer,
    DeferredTrailIngestionSink sink,
    IngressCommitWriter commitWriter,
    IngressDeadLetterWriter deadLetterWriter,
    ILogger<KafkaIngressProcessor> logger) : IKafkaMessageProcessor
{
    /// <summary>Header the producer stamps with its logical name.</summary>
    private const string ProducerHeader = "producer";

    public string Consumer => IngressCommitWriter.ConsumerName;

    public async Task<KafkaDisposition> ProcessAsync(
        KafkaMessageContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Event is not { } cloudEvent)
        {
            return await RefuseAsync(
                context,
                new DeadLetterDiagnosis
                {
                    Reason = NotificationRejectionReasons.PayloadInvalid,
                    Producer = HeaderOrNull(context, ProducerHeader),
                },
                cancellationToken);
        }

        var producer = HeaderOrNull(context, ProducerHeader) ?? cloudEvent.Source;
        if (IngressRequestBinder.Bind(cloudEvent.Data) is not { } request)
        {
            return await RefuseAsync(
                context,
                new DeadLetterDiagnosis
                {
                    Reason = NotificationRejectionReasons.PayloadInvalid,
                    Producer = producer,
                },
                cancellationToken);
        }

        // The kill switch of the design has no table in this phase; when it
        // arrives it is evaluated here, ahead of the registry.
        ProducerAuthorization authorization = await authorizer.AuthorizeAsync(
            producer, request.Command.Application, request.Command.Class, cancellationToken);

        Result<RequestNotification.Outcome> result = await handler.HandleAsync(
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
        RequestNotification.Outcome outcome,
        CancellationToken cancellationToken)
    {
        switch (outcome)
        {
            case RequestNotification.Outcome.Accepted accepted:
                logger.IngressEventAccepted(context.Topic, context.Partition, context.Offset, accepted.NotificationId);
                return await CommitAsync(context, new KafkaDisposition.Processed(), cancellationToken);

            case RequestNotification.Outcome.Replayed replayed:
                logger.IngressEventReplayed(context.Topic, context.Partition, context.Offset, replayed.NotificationId);
                return await CommitAsync(context, new KafkaDisposition.Duplicate(), cancellationToken);

            case RequestNotification.Outcome.SensitiveVariablesOnBus sensitive:
                return await RefuseAsync(
                    context,
                    Diagnose(request, producer, NotificationRejectionReasons.SensitiveVariablesOnBus)
                        with { RedactedVariableNames = sensitive.VariableNames },
                    cancellationToken);

            case RequestNotification.Outcome.ProducerNotAuthorized denied:
                return await RefuseAsync(
                    context, Diagnose(request, producer, denied.Reason), cancellationToken);

            case RequestNotification.Outcome.TemplateRejected rejected:
                return await RefuseAsync(
                    context, Diagnose(request, producer, rejected.Reason), cancellationToken);

            case RequestNotification.Outcome.PayloadInvalid:
                return await RefuseAsync(
                    context,
                    Diagnose(request, producer, NotificationRejectionReasons.PayloadInvalid),
                    cancellationToken);

            case RequestNotification.Outcome.IdempotencyConflict:
                return await RefuseAsync(
                    context,
                    Diagnose(request, producer, NotificationRejectionReasons.IdempotencyKeyConflict),
                    cancellationToken);

            case RequestNotification.Outcome.RateLimited:
                // Only the recipient budget rejects on this path: the
                // principal dimension is counted and observed, never refused.
                return await RefuseAsync(
                    context,
                    Diagnose(request, producer, NotificationRejectionReasons.RecipientRateLimited),
                    cancellationToken);

            default:
                throw new InvalidOperationException(
                    $"Desfecho de ingestão não suportado: {outcome.GetType().Name}.");
        }
    }

    /// <summary>
    /// Records the refusal on the dead-letter topic and only then commits the
    /// trail and the deduplication mark. The order is the whole point: a mark
    /// written first would make the replay of a crash skip a record nobody
    /// ever put on the dead-letter topic.
    /// </summary>
    private async Task<KafkaDisposition> RefuseAsync(
        KafkaMessageContext context,
        DeadLetterDiagnosis diagnosis,
        CancellationToken cancellationToken)
    {
        await deadLetterWriter.ProduceAsync(context, diagnosis, cancellationToken);
        return await CommitAsync(context, new KafkaDisposition.DeadLetter(diagnosis.Reason), cancellationToken);
    }

    private async Task<KafkaDisposition> CommitAsync(
        KafkaMessageContext context,
        KafkaDisposition disposition,
        CancellationToken cancellationToken)
    {
        var committed = await commitWriter.TryCommitAsync(context.DedupeId, sink, cancellationToken);
        return committed ? disposition : new KafkaDisposition.Duplicate();
    }

    /// <summary>
    /// The provenance of one bus request as the trail must record it. The
    /// coordinates travel opaque: the use case writes them and never reads
    /// them, and they are what lets a disputed request be checked against the
    /// record the broker still holds.
    /// </summary>
    private static RequestNotification.IngestionOrigin OriginOf(
        KafkaMessageContext context,
        CloudEvent cloudEvent)
        => new()
        {
            Source = RequestNotification.IngestionSource.Kafka,
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

    private static string? HeaderOrNull(KafkaMessageContext context, string name)
        => context.Headers.TryGetValue(name, out var value) && value.Length > 0 ? value : null;
}

/// <summary>One bus request bound to the ingestion command, with its idempotency scope.</summary>
internal sealed record IngressRequest(RequestNotification.Command Command, string IdempotencyKey);

/// <summary>
/// Binds the event body to the ingestion command. Binding is deliberately
/// permissive about values and strict about the idempotency key: a missing or
/// mistyped field becomes an empty value the shared validator refuses with a
/// field-level report, while a request without an idempotency key has no
/// scope to be idempotent in and cannot be bound at all.
/// </summary>
internal static class IngressRequestBinder
{
    public static IngressRequest? Bind(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (ReadString(data, "idempotencyKey") is not { Length: > 0 } idempotencyKey)
        {
            return null;
        }

        var command = new RequestNotification.Command(
            ReadString(data, "application") ?? string.Empty,
            ReadString(data, "recipientId") ?? string.Empty,
            ReadString(data, "class") ?? string.Empty,
            ReadString(data, "templateKey") ?? string.Empty,
            ReadString(data, "locale") ?? string.Empty,
            ReadInt32(data, "ttlSeconds"))
        {
            Variables = ReadObject(data, "variables"),
            Metadata = ReadObject(data, "metadata"),
            ChannelsHint = ReadStringArray(data, "channelsHint"),
            CorrelationId = ReadString(data, "correlationId"),
            ScheduledAt = ReadDateTimeOffset(data, "scheduledAt"),
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

    private static JsonElement? ReadObject(JsonElement data, string name)
        => data.TryGetProperty(name, out JsonElement element) && element.ValueKind == JsonValueKind.Object
            ? element.Clone()
            : null;

    private static IReadOnlyList<string>? ReadStringArray(JsonElement data, string name)
    {
        if (!data.TryGetProperty(name, out JsonElement element) || element.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        return [.. element.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString()!)];
    }

    private static DateTimeOffset? ReadDateTimeOffset(JsonElement data, string name)
        => data.TryGetProperty(name, out JsonElement element)
            && element.ValueKind == JsonValueKind.String
            && element.TryGetDateTimeOffset(out DateTimeOffset value)
                ? value
                : null;
}
