using System.ComponentModel.DataAnnotations;

namespace NotificationHub.Api.Modules.Notifications.Infrastructure.Consuming;

/// <summary>
/// Topology of the bus ingress: which topic carries producer requests, where a
/// permanently invalid one is recorded, and under which consumer group this
/// role identifies itself.
/// </summary>
public sealed class KafkaIngressOptions
{
    public const string SectionName = "Modules:Notifications:KafkaIngress";

    [Required]
    public string RequestedTopic { get; init; } = "notifications.requested.v1";

    [Required]
    public string DeadLetterTopic { get; init; } = "notifications.requested.dlt";

    [Required]
    public string ConsumerGroup { get; init; } = "notification-hub-ingress";
}
