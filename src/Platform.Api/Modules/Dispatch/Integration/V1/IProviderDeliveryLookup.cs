using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.Dispatch.Integration.V1;

/// <summary>
/// Everything a provider may need to find one message it was given, and
/// nothing else. The record is the pull-side twin of the callback: the caller
/// states what it knows about a send whose outcome never came back, and the
/// adapter decides which of those facts its provider can search by.
/// </summary>
/// <param name="Correlation">
/// Attempt identifiers of the send, the same pair the dispatch handed the
/// provider. A provider that echoes custom metadata searches by exactly this.
/// </param>
/// <param name="ProviderMessageId">
/// The provider's own identity for the message, when acceptance produced one.
/// It is the preferred route wherever it exists, because it names one message
/// instead of describing a set that might hold more than one.
/// </param>
/// <param name="Target">
/// Destination of the send, for the providers that search by neither metadata
/// nor identity and can only be asked what they sent to an address around an
/// instant. It is optional for that reason alone, it is personal data, and it
/// is meant to be resolved at the moment of the query and discarded with the
/// query: no adapter may persist it, log it or echo it back.
/// </param>
/// <param name="SentAt">
/// When this hub handed the message to the provider, as well as it knows: the
/// send stamp where a verdict produced one, and the instant the attempt was
/// last known to be with the provider otherwise. It anchors the time window of
/// a search by destination and bounds the history a search has to read.
/// </param>
public sealed record ProviderDeliveryQuery(
    DispatchCorrelation Correlation,
    string? ProviderMessageId,
    DeliveryTarget? Target,
    DateTimeOffset SentAt);

/// <summary>
/// Port over one provider's answer to a question the provider was never asked
/// to volunteer: what happened to a message whose feedback never arrived. It
/// is the pull half of delivery feedback, and it deliberately returns the very
/// same <see cref="ProviderDeliveryEvent"/> the push half returns.
/// <para>
/// One vocabulary and one state machine is the whole point. A reconciliation
/// with a canonical form of its own would be a second machine describing the
/// same attempt, free to conclude something the callback path would never
/// conclude, and the divergence would show up as attempts in states no reader
/// can explain.
/// </para>
/// <para>
/// A provider that offers no later lookup registers no implementation at all.
/// That is a statement, not an omission: an adapter that answered "nothing
/// found" for such a provider would make an attempt nobody can settle look
/// like an attempt the provider denies, and the two deserve different records.
/// </para>
/// <para>
/// The lookup reads and never sends. Its failures therefore say nothing about
/// the send path: they are not a provider verdict, they do not feed a circuit
/// that measures sends, and an empty answer is a success with no events.
/// </para>
/// </summary>
public interface IProviderDeliveryLookup
{
    /// <summary>
    /// Stable provider identity, the same key the sending adapter, the
    /// interpreter and the provider configuration rows use, so the three
    /// halves of one provider agree on who the provider is.
    /// </summary>
    string ProviderKey { get; }

    /// <summary>
    /// Asks the provider about one message. Every refusal comes back as a
    /// failed result carrying a <see cref="ProviderLookupRefusal"/> code,
    /// because a batch job settles a failed lookup by trying again tomorrow
    /// and an exception would erase which of its rows it still owes an answer
    /// for. An empty list is a success: the provider has nothing to say about
    /// this message, which is different from being unable to answer.
    /// </summary>
    Task<Result<IReadOnlyList<ProviderDeliveryEvent>>> LookupAsync(
        ProviderDeliveryQuery query,
        CancellationToken cancellationToken);
}

/// <summary>
/// Resolves the lookup that speaks for a provider key. Unlike the send-side
/// resolution, an unmatched key here is an ordinary and expected answer: the
/// hub has a provider whose platform offers no way to ask afterwards, and the
/// caller records that the attempt stays unsettled instead of treating it as a
/// deployment defect.
/// </summary>
public interface IProviderDeliveryLookupResolver
{
    /// <summary>Resolves the lookup registered under <paramref name="providerKey"/>.</summary>
    Result<IProviderDeliveryLookup> Resolve(string providerKey);
}
