namespace NotificationHub.Api.Modules.Dispatch.Integration.V1;

/// <summary>
/// Attempt identifiers the caller hands to a provider for later webhook
/// reconciliation. Pure pass-through: the identifiers never enter the
/// rendered content nor its audited hashes, and an adapter whose provider
/// has no correlation field ignores them entirely.
/// </summary>
public sealed record DispatchCorrelation(Guid NotificationId, Guid AttemptId);

/// <summary>
/// One send to one provider: the destination and the rendered content of a
/// single attempt. The envelope exists so attempt-scoped data that providers
/// need later (correlation identifiers for webhook reconciliation, for
/// example) can join as optional members without breaking the send signature.
/// Target and message must describe the same channel; a mismatch is a caller
/// defect and adapters reject it with an exception, not a result.
/// </summary>
/// <param name="Target">Destination of this single send.</param>
/// <param name="Message">Rendered content, final: an adapter never rewrites it.</param>
/// <param name="Correlation">Attempt identifiers for later webhook reconciliation.</param>
/// <param name="Validity">
/// How long the message is still worth delivering, counted from now. It is the
/// remaining validity of the notification, computed by the caller that owns
/// that state, and providers able to expire a queued message receive it as
/// their own validity knob. Null means the caller states nothing, and an
/// adapter then lets the provider apply its own default; deciding not to call
/// a provider at all belongs to the caller, never here.
/// </param>
/// <param name="Application">
/// The calling application this send belongs to, as the hub names it. It is
/// here for providers whose sending identity is allocated per application, such
/// as a sender pool bound to one brand, and it never enters the rendered
/// content nor its audited hashes. Null means the caller states nothing, and an
/// adapter then uses the sending identity of the deployment.
/// </param>
public sealed record DispatchRequest(
    DeliveryTarget Target,
    RenderedMessage Message,
    DispatchCorrelation? Correlation = null,
    TimeSpan? Validity = null,
    string? Application = null);
