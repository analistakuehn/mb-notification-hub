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
public sealed record DispatchRequest(
    DeliveryTarget Target,
    RenderedMessage Message,
    DispatchCorrelation? Correlation = null);
