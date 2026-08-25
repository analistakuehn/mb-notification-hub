namespace NotificationHub.Api.Modules.Dispatch.Integration.V1;

/// <summary>
/// One inbound provider callback exactly as it arrived, before anything in
/// this process trusts it. The body travels as raw bytes on purpose: every
/// signature scheme verified here signs the precise octets the provider sent,
/// and a round trip through a string or a parsed model re-encodes them and
/// invalidates the proof. The request URL and the remote address are
/// authentication material rather than diagnostics, because one scheme folds
/// the URL into the signed payload and origin allowlisting reads the address.
/// </summary>
public sealed record ProviderWebhookRequest(
    string ProviderKey,
    string RequestUrl,
    IReadOnlyDictionary<string, string> Headers,
    string? RemoteIpAddress,
    ReadOnlyMemory<byte> Body);

/// <summary>
/// A callback whose authenticity this process proved. Only the provider
/// identity, the instant of the proof and the verified bytes survive:
/// headers and origin exhaust their meaning at the proof, so they do not
/// travel into interpretation where a later reader could mistake unverified
/// material for content. Holding an instance is the claim that the bytes came
/// from the named provider, so it is never constructed outside verification.
/// </summary>
public sealed record VerifiedProviderWebhook(
    string ProviderKey,
    DateTimeOffset VerifiedAt,
    ReadOnlyMemory<byte> Body);
