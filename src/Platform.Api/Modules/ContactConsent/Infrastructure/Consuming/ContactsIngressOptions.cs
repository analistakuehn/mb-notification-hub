using System.ComponentModel.DataAnnotations;

namespace NotificationHub.Api.Modules.ContactConsent.Infrastructure.Consuming;

/// <summary>
/// Topology and accepted actor vocabulary of the contact ingestion: which
/// topic carries the declarations, where a permanently invalid one is
/// recorded, under which consumer group the role identifies itself, and which
/// CloudEvents sources it accepts.
///
/// <see cref="AcceptedSources"/> is not a second copy of the broker ACL. The
/// REST route identifies its writer by the application id of the token, and a
/// record from the bus needs an identity just as stable for the actor of the
/// consent ledger and for the trail; the accepted source is that identity. An
/// empty list refuses the boot, because a role that accepts no source would
/// subscribe to the topic only to dead-letter every record on it.
/// </summary>
public sealed class ContactsIngressOptions
{
    public const string SectionName = "Modules:ContactConsent:KafkaIngress";

    [Required]
    public string ContactsTopic { get; init; } = "contacts.events.v1";

    [Required]
    public string DeadLetterTopic { get; init; } = "contacts.events.dlt";

    [Required]
    public string ConsumerGroup { get; init; } = "notification-hub-contacts-ingress";

    /// <summary>URNs of the CloudEvents sources this role accepts.</summary>
    [MinLength(1)]
    public string[] AcceptedSources { get; init; } = [];
}
