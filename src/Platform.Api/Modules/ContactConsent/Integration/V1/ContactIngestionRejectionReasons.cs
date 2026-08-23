namespace NotificationHub.Api.Modules.ContactConsent.Integration.V1;

/// <summary>
/// Refusal vocabulary of the contact ingestion, published because the
/// dead-letter record is a contract with the team that emits the
/// declarations: whoever reads the refusal has to know, from the reason
/// alone, what to fix and whether to re-emit.
///
/// It is a vocabulary of its own, not a borrowed one. The notification
/// rejection catalog answers questions about a notification request (its
/// template, its class, its idempotency scope) and none of them exists here,
/// while the answers this transport must give (an unknown recipient, a channel
/// with no contact point) have no meaning there.
/// </summary>
public static class ContactIngestionRejectionReasons
{
    /// <summary>The CloudEvents source of the record is outside the accepted list of the role.</summary>
    public const string SourceNotAuthorized = "source-not-authorized";

    /// <summary>The body is structurally invalid, or fails the same shape rules the REST route applies.</summary>
    public const string PayloadInvalid = "payload-invalid";

    /// <summary>The event type is not one this hub consumes on the contacts topic.</summary>
    public const string EventTypeUnsupported = "event-type-unsupported";

    /// <summary>The declaration targets a recipient with no contact registration at all.</summary>
    public const string RecipientUnknown = "recipient-unknown";

    /// <summary>A declared consent names a channel with no active contact point to anchor it.</summary>
    public const string NoContactPointForChannel = "no-contact-point-for-channel";

    /// <summary>Every member, for contract checks and for operator tooling.</summary>
    public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        SourceNotAuthorized,
        PayloadInvalid,
        EventTypeUnsupported,
        RecipientUnknown,
        NoContactPointForChannel,
    };

    public static bool IsCanonical(string? reason) => reason is not null && All.Contains(reason);
}
