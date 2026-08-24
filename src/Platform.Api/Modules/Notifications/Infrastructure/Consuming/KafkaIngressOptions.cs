using System.ComponentModel.DataAnnotations;

namespace NotificationHub.Api.Modules.Notifications.Infrastructure.Consuming;

/// <summary>
/// Topology of the bus ingress: the exclusive topic of each logical producer,
/// where a permanently invalid request is recorded, and under which consumer
/// group this role identifies itself.
/// </summary>
public sealed class KafkaIngressOptions
{
    public const string SectionName = "Modules:Notifications:KafkaIngress";

    [MinLength(1)]
    public List<KafkaIngressBindingOptions> Bindings { get; init; } = [];

    [Required]
    public string DeadLetterTopic { get; init; } = "notifications.requested.dlt";

    [Required]
    public string ConsumerGroup { get; init; } = "notification-hub-ingress";
}

/// <summary>
/// One exclusive ingress topic and the logical producer authenticated by the
/// broker ACL that grants write access to it.
/// </summary>
public sealed class KafkaIngressBindingOptions
{
    [Required]
    public string Topic { get; init; } = string.Empty;

    [Required]
    public string LogicalProducer { get; init; } = string.Empty;
}
