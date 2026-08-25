using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.Dispatch.Integration.V1;

/// <summary>
/// Port over one provider's delivery feedback. It answers two questions and
/// no others: did this provider really send these bytes, and what does the
/// provider's dialect mean in the canonical vocabulary. It knows no attempt,
/// no notification, no deduplication and no persistence; correlating a canonical
/// event with the row it describes belongs to the module that owns that row.
/// <para>
/// The two questions are separate members because they happen at different
/// moments: authentication runs before the endpoint, on bytes nobody trusts
/// yet, while interpretation runs inside it on bytes already proven. Folding
/// them together would either read the body twice or force the endpoint to
/// trust an unverified parse.
/// </para>
/// <para>
/// Neither member throws for bad input. Every refusal returns as a failed
/// result carrying a <see cref="ProviderWebhookRefusal"/> code, because the
/// input is hostile by assumption and an exception would erase the caller's
/// ability to distinguish a forged origin from a rotated secret. Both members
/// are synchronous: verification is arithmetic, and interpretation is parsing.
/// </para>
/// </summary>
public interface IProviderWebhookInterpreter
{
    /// <summary>
    /// Stable provider identity, the same key the sending adapter and the
    /// provider configuration rows use, so feedback and sends agree on who
    /// the provider is.
    /// </summary>
    string ProviderKey { get; }

    /// <summary>
    /// Whether this provider's signature covers the callback URL, and with it
    /// anything the hub asked the provider to carry in the route.
    /// <para>
    /// It decides whether correlation read from the route may be believed. A
    /// provider that signs only a timestamp and a body proves the body came
    /// from it and proves nothing at all about the address the body arrived
    /// at, so route correlation from such a provider is an unsigned claim
    /// about which attempt an authentic callback describes. Believing it lets
    /// a genuine callback be steered onto another attempt, changing its state,
    /// its fallback and its suppression effects.
    /// </para>
    /// <para>
    /// It is a property of the provider's signing scheme, not a configuration
    /// knob: an operator cannot make a signature cover more than it covers.
    /// </para>
    /// </summary>
    bool SignatureCoversRoute { get; }

    /// <summary>
    /// Proves the callback came from this provider, unchanged, recently
    /// enough and from an allowed origin.
    /// </summary>
    Result<VerifiedProviderWebhook> Verify(ProviderWebhookRequest request);

    /// <summary>
    /// Translates verified bytes into canonical events. One callback carries
    /// one event for some providers and a batch for others, so the result is
    /// always a list and an empty batch is a success with no events.
    /// </summary>
    Result<IReadOnlyList<ProviderDeliveryEvent>> Interpret(VerifiedProviderWebhook webhook);
}

/// <summary>
/// Resolves the interpreter that speaks for a provider key taken from an
/// inbound request. The key arrives from outside, so an unknown value is
/// ordinary traffic rather than a deployment defect, and it comes back as the
/// <see cref="ProviderWebhookRefusal.ProviderUnknown"/> refusal instead of an
/// exception.
/// </summary>
public interface IProviderWebhookInterpreterResolver
{
    /// <summary>Resolves the interpreter registered under <paramref name="providerKey"/>.</summary>
    Result<IProviderWebhookInterpreter> Resolve(string providerKey);
}
