using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using NotificationHub.Api.Infrastructure.Messaging.Consuming;
using NotificationHub.Api.Modules.Notifications.Integration.V1;

namespace NotificationHub.Api.Modules.Notifications.Infrastructure.Consuming;

/// <summary>What the dead-letter record must say about one refused bus event.</summary>
internal sealed record DeadLetterDiagnosis
{
    /// <summary>Canonical rejection reason.</summary>
    public required string Reason { get; init; }

    public string? Producer { get; init; }

    public string? Application { get; init; }

    public string? Class { get; init; }

    public string? IdempotencyKey { get; init; }

    /// <summary>
    /// Names of the sensitive variables the template declares. Present only
    /// for the bus restriction, and the trigger for replacing the variables of
    /// the published body with these names.
    /// </summary>
    public IReadOnlyList<string>? RedactedVariableNames { get; init; }
}

/// <summary>
/// Records one permanently invalid bus event on the dead-letter topic with the
/// diagnostics the producing team needs to fix it.
///
/// Before producer trust, and when that trust is explicitly denied or disabled,
/// the body is rebuilt from an allow-list of safe diagnostics. Payload values
/// never travel to a topic with longer retention. The sensitive-variable
/// restriction keeps its narrower behavior: only the variables object is
/// replaced by the names the template declares. Refusals after producer trust
/// retain the original body unless that restriction applies. A header announces
/// either form of redaction so nobody mistakes the record for a faithful copy.
/// </summary>
internal sealed class IngressDeadLetterWriter(
    IKafkaDeadLetterProducer producer,
    IOptions<KafkaIngressOptions> options,
    TimeProvider timeProvider,
    ILogger<IngressDeadLetterWriter> logger)
{
    private const string DataProperty = "data";
    private const string VariablesProperty = "variables";

    // Only the headers of this contract live here; the transport headers every
    // dead-letter record carries belong to the platform.
    internal const string ProducerHeader = "producer";
    internal const string ApplicationHeader = "application";
    internal const string ClassHeader = "class";
    internal const string IdempotencyKeyHeader = "idempotencyKey";

    public async Task ProduceAsync(
        KafkaMessageContext context,
        DeadLetterDiagnosis diagnosis,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(diagnosis);

        var redactPayload = RequiresPayloadRedaction(diagnosis.Reason);
        var redactVariables = diagnosis.RedactedVariableNames is not null;
        var redacted = redactPayload || redactVariables;
        var headers = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [DeadLetterHeaders.Reason] = diagnosis.Reason,
            [DeadLetterHeaders.SourceTopic] = context.Topic,
            [DeadLetterHeaders.SourcePartition] =
                context.Partition.ToString(CultureInfo.InvariantCulture),
            [DeadLetterHeaders.SourceOffset] =
                context.Offset.ToString(CultureInfo.InvariantCulture),
            [DeadLetterHeaders.OccurredAt] = timeProvider.GetUtcNow().ToString("O"),
            [DeadLetterHeaders.Redacted] = redacted ? "true" : "false",
        };
        AddWhenPresent(headers, ProducerHeader, diagnosis.Producer);
        if (!redactPayload)
        {
            AddWhenPresent(headers, ApplicationHeader, diagnosis.Application);
            AddWhenPresent(headers, ClassHeader, diagnosis.Class);
            AddWhenPresent(headers, IdempotencyKeyHeader, diagnosis.IdempotencyKey);
            AddWhenPresent(
                headers,
                DeadLetterHeaders.Traceparent,
                context.Event?.Traceparent
                    ?? (context.Headers.TryGetValue(DeadLetterHeaders.Traceparent, out var traceparent)
                        ? traceparent
                        : null));
        }

        await producer.ProduceAsync(
            new DeadLetterRecord
            {
                Topic = options.Value.DeadLetterTopic,
                Key = redactPayload ? diagnosis.Producer : context.Key,
                Body = redactPayload
                    ? Summarize(context, diagnosis)
                    : redactVariables
                        ? RedactVariables(context.Body, diagnosis.RedactedVariableNames!)
                        : context.Body,
                Headers = headers,
            },
            cancellationToken);

        // After the produce, never before: the line claims a record exists.
        logger.DeadLetterProduced(
            options.Value.DeadLetterTopic,
            diagnosis.Reason,
            context.Topic,
            context.Partition,
            context.Offset,
            diagnosis.Producer,
            redactPayload ? null : diagnosis.Application,
            redacted);
    }

    /// <summary>
    /// Rebuilds a refusal from the diagnostic allow-list. The raw envelope is
    /// deliberately not an input, so variables, metadata, subjects, sources,
    /// and future payload fields cannot cross this boundary by accident.
    /// </summary>
    internal static string Summarize(
        KafkaMessageContext context,
        DeadLetterDiagnosis diagnosis)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(diagnosis);

        var summary = new JsonObject
        {
            [DeadLetterHeaders.Reason] = diagnosis.Reason,
            [DeadLetterHeaders.SourceTopic] = context.Topic,
            [DeadLetterHeaders.SourcePartition] = context.Partition,
            [DeadLetterHeaders.SourceOffset] = context.Offset,
        };
        AddWhenPresent(summary, ProducerHeader, diagnosis.Producer);
        return summary.ToJsonString();
    }

    /// <summary>
    /// Replaces the variables object of the published body with the names the
    /// template declares. A body that cannot be parsed loses its data section
    /// entirely: when in doubt about where the values are, nothing goes.
    /// </summary>
    internal static string RedactVariables(string body, IReadOnlyList<string> variableNames)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(body);
        }
        catch (JsonException)
        {
            root = null;
        }

        if (root is not JsonObject envelope || envelope[DataProperty] is not JsonObject data)
        {
            return JsonSerializer.Serialize(new { redactedVariables = variableNames });
        }

        data[VariablesProperty] = new JsonArray([.. variableNames.Select(name => JsonValue.Create(name))]);
        return envelope.ToJsonString();
    }

    private static bool RequiresPayloadRedaction(string reason)
        => reason is NotificationRejectionReasons.PayloadInvalid
            or NotificationRejectionReasons.EventTypeUnsupported
            or NotificationRejectionReasons.ProducerDisabled
            or NotificationRejectionReasons.ProducerNotAuthorized;

    private static void AddWhenPresent(JsonObject body, string name, string? value)
    {
        if (value is { Length: > 0 })
        {
            body[name] = value;
        }
    }

    private static void AddWhenPresent(Dictionary<string, string> headers, string name, string? value)
    {
        if (value is { Length: > 0 })
        {
            headers[name] = value;
        }
    }
}
