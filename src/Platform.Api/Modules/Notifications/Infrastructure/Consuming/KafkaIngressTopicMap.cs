using Microsoft.Extensions.Options;

namespace NotificationHub.Api.Modules.Notifications.Infrastructure.Consuming;

/// <summary>
/// Validates the one-to-one trust binding between ingress topics and logical
/// producers. Topic and producer comparisons stay ordinal because both are
/// broker and registry identifiers, not human text.
/// </summary>
internal sealed class KafkaIngressOptionsValidator : IValidateOptions<KafkaIngressOptions>
{
    public ValidateOptionsResult Validate(string? name, KafkaIngressOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        List<string> failures = Failures(options);
        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    internal static List<string> Failures(KafkaIngressOptions options)
    {
        var failures = new List<string>();
        if (string.IsNullOrWhiteSpace(options.ConsumerGroup))
        {
            failures.Add($"{nameof(KafkaIngressOptions.ConsumerGroup)} não pode ser vazio.");
        }

        var deadLetterTopic = options.DeadLetterTopic?.Trim();
        if (string.IsNullOrWhiteSpace(deadLetterTopic))
        {
            failures.Add($"{nameof(KafkaIngressOptions.DeadLetterTopic)} não pode ser vazio.");
        }

        if (options.Bindings is not { Count: > 0 })
        {
            failures.Add(
                $"{nameof(KafkaIngressOptions.Bindings)} exige ao menos um binding Topic + LogicalProducer.");
            return failures;
        }

        var topics = new HashSet<string>(StringComparer.Ordinal);
        var producers = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < options.Bindings.Count; index++)
        {
            KafkaIngressBindingOptions? binding = options.Bindings[index];
            if (binding is null)
            {
                failures.Add($"{nameof(KafkaIngressOptions.Bindings)}[{index}] não pode ser nulo.");
                continue;
            }

            var topic = binding.Topic?.Trim();
            var producer = binding.LogicalProducer?.Trim();
            if (string.IsNullOrWhiteSpace(topic))
            {
                failures.Add(
                    $"{nameof(KafkaIngressOptions.Bindings)}[{index}].{nameof(binding.Topic)} não pode ser vazio.");
            }
            else
            {
                if (!topics.Add(topic))
                {
                    failures.Add($"Tópico de entrada duplicado ou ambíguo: '{topic}'.");
                }

                if (string.Equals(topic, deadLetterTopic, StringComparison.Ordinal))
                {
                    failures.Add(
                        $"O tópico de entrada '{topic}' não pode ser o tópico de dead-letter.");
                }
            }

            if (string.IsNullOrWhiteSpace(producer))
            {
                failures.Add(
                    $"{nameof(KafkaIngressOptions.Bindings)}[{index}].{nameof(binding.LogicalProducer)} não pode ser vazio.");
            }
            else if (!producers.Add(producer))
            {
                failures.Add($"Produtor lógico duplicado ou ambíguo: '{producer}'.");
            }
        }

        return failures;
    }
}

/// <summary>
/// Immutable authoritative identity map used by both worker composition and
/// message processing. Payload and headers never participate in this lookup.
/// </summary>
internal sealed class KafkaIngressTopicMap
{
    private readonly Dictionary<string, string> _producerByTopic;

    private KafkaIngressTopicMap(KafkaIngressOptions options)
    {
        ConsumerGroup = options.ConsumerGroup.Trim();
        Topics = [.. options.Bindings.Select(binding => binding.Topic.Trim())];
        _producerByTopic = options.Bindings.ToDictionary(
            binding => binding.Topic.Trim(),
            binding => binding.LogicalProducer.Trim(),
            StringComparer.Ordinal);
    }

    public string ConsumerGroup { get; }

    public IReadOnlyList<string> Topics { get; }

    public static KafkaIngressTopicMap Create(KafkaIngressOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        List<string> failures = KafkaIngressOptionsValidator.Failures(options);
        if (failures.Count > 0)
        {
            throw new OptionsValidationException(
                Options.DefaultName, typeof(KafkaIngressOptions), failures);
        }

        return new KafkaIngressTopicMap(options);
    }

    public string ResolveLogicalProducer(string topic)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(topic);

        return _producerByTopic.TryGetValue(topic, out var logicalProducer)
            ? logicalProducer
            : throw new InvalidOperationException(
                $"O tópico de entrada '{topic}' não possui produtor lógico configurado.");
    }
}
