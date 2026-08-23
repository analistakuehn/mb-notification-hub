using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using NotificationHub.Api.Infrastructure.Messaging.Consuming;

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
/// The body is the original one for every reason but the sensitive-variable
/// restriction. There the control would defeat itself: the entry topic keeps
/// records for a day and the dead-letter topic for two weeks, so copying the
/// refused body verbatim would move the secret to a topic that holds it
/// fourteen times longer. For that reason alone the variables object is
/// replaced by the list of variable names the template declares, values never
/// travel, and a header announces the redaction so nobody mistakes the record
/// for a faithful copy on redrive.
/// </summary>
internal sealed class IngressDeadLetterWriter(
    IKafkaDeadLetterProducer producer,
    IOptions<KafkaIngressOptions> options,
    TimeProvider timeProvider,
    ILogger<IngressDeadLetterWriter> logger)
{
    private const string DataProperty = "data";
    private const string VariablesProperty = "variables";

    internal const string ReasonHeader = "reason";
    internal const string SourceTopicHeader = "sourceTopic";
    internal const string SourcePartitionHeader = "sourcePartition";
    internal const string SourceOffsetHeader = "sourceOffset";
    internal const string ProducerHeader = "producer";
    internal const string ApplicationHeader = "application";
    internal const string ClassHeader = "class";
    internal const string IdempotencyKeyHeader = "idempotencyKey";
    internal const string OccurredAtHeader = "occurredAt";
    internal const string TraceparentHeader = "traceparent";
    internal const string RedactedHeader = "redacted";

    public async Task ProduceAsync(
        KafkaMessageContext context,
        DeadLetterDiagnosis diagnosis,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(diagnosis);

        var redacted = diagnosis.RedactedVariableNames is not null;
        var headers = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ReasonHeader] = diagnosis.Reason,
            [SourceTopicHeader] = context.Topic,
            [SourcePartitionHeader] = context.Partition.ToString(System.Globalization.CultureInfo.InvariantCulture),
            [SourceOffsetHeader] = context.Offset.ToString(System.Globalization.CultureInfo.InvariantCulture),
            [OccurredAtHeader] = timeProvider.GetUtcNow().ToString("O"),
            [RedactedHeader] = redacted ? "true" : "false",
        };
        AddWhenPresent(headers, ProducerHeader, diagnosis.Producer);
        AddWhenPresent(headers, ApplicationHeader, diagnosis.Application);
        AddWhenPresent(headers, ClassHeader, diagnosis.Class);
        AddWhenPresent(headers, IdempotencyKeyHeader, diagnosis.IdempotencyKey);
        AddWhenPresent(
            headers,
            TraceparentHeader,
            context.Event?.Traceparent
                ?? (context.Headers.TryGetValue(TraceparentHeader, out var traceparent) ? traceparent : null));

        await producer.ProduceAsync(
            new DeadLetterRecord
            {
                Topic = options.Value.DeadLetterTopic,
                Key = context.Key,
                Body = redacted
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
            diagnosis.Application,
            redacted);
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

    private static void AddWhenPresent(Dictionary<string, string> headers, string name, string? value)
    {
        if (value is { Length: > 0 })
        {
            headers[name] = value;
        }
    }
}
