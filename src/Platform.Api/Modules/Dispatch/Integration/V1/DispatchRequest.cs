using NotificationHub.Api.Modules.AttachmentManagement.Integration.V1;

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
/// <param name="Attachments">
/// The attachment set this send carries, in the order it was claimed in, in
/// the neutral form the owning module publishes: composition, the values each
/// member was released under, and an opaque handle for the accepted content.
/// No store, no key, no generation, no address and no digest travels with it,
/// so nothing here can be exchanged for the bytes and nothing here has to be
/// kept in step with a record it no longer sits beside.
/// <para>
/// It arrives already verified. Whether every member may still leave, and
/// whether the set still fits what one notification may carry, are settled by
/// the caller in the window before this call, on the module that owns those
/// rules; an adapter that asked again would be a second authority over the
/// same question, answering from a value that says nothing about eligibility.
/// What an adapter does with the set is compose it for its provider, and a
/// provider that cannot carry the set is refused by the route that planned
/// the send, never quietly downgraded here.
/// </para>
/// <para>
/// Null is the whole way to say the send carries no attachment at all, because
/// a set with no members is not a value this contract can hold. It is
/// therefore unlike the optional members above: they leave a choice to the
/// provider, and this one leaves nothing to choose.
/// </para>
/// </param>
public sealed record DispatchRequest(
    DeliveryTarget Target,
    RenderedMessage Message,
    DispatchCorrelation? Correlation = null,
    TimeSpan? Validity = null,
    string? Application = null,
    AcceptedAttachmentSet? Attachments = null);
