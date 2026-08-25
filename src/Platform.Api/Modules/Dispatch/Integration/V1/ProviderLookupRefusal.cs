using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.Dispatch.Integration.V1;

/// <summary>
/// Closed catalogue of reasons this module cannot answer what happened to a
/// message. A refusal is data for the same reason a callback refusal is: the
/// caller is a batch job that owns a set of unsettled rows, and it has to know
/// which ones it may retry tomorrow and which ones no retry will ever answer.
/// <para>
/// The two are kept apart on purpose. <see cref="LookupUnsupported"/> is
/// permanent and is not a fault: the provider platform offers no way to ask
/// after the fact, so the attempt stays unsettled with a record and nobody is
/// paged. <see cref="LookupUnavailable"/> is a fault of the moment, and the
/// same query put again later may well answer.
/// </para>
/// <para>
/// A refusal carries the code as its whole error text, with nothing appended.
/// What an adapter could append at that point is either the destination or the
/// provider's own error prose, and neither belongs in a value the caller
/// records.
/// </para>
/// </summary>
public static class ProviderLookupRefusal
{
    /// <summary>No adapter in this process speaks for the named provider.</summary>
    public const string ProviderUnknown = "provider-unknown";

    /// <summary>
    /// The provider offers no lookup after the send. Permanent by nature: an
    /// attempt of this provider is settled by fallback and by validity, never
    /// by asking.
    /// </summary>
    public const string LookupUnsupported = "lookup-unsupported";

    /// <summary>The provider could not be reached or refused to answer this time.</summary>
    public const string LookupUnavailable = "lookup-unavailable";

    /// <summary>
    /// The message is older than the history this provider still holds for
    /// this account. Permanent for the row and unrelated to the provider being
    /// healthy: how far back the history reaches is a commercial term of the
    /// contracted plan, and it is configuration for exactly that reason.
    /// </summary>
    public const string HistoryExhausted = "history-exhausted";

    /// <summary>The provider answered in a shape this adapter cannot read.</summary>
    public const string PayloadUnreadable = "payload-unreadable";

    /// <summary>
    /// The query states nothing this provider can search by. It is a caller
    /// defect in the making rather than a provider fault, and it is separable
    /// because the cure is different: the caller has to carry more about the
    /// attempt, not try again.
    /// </summary>
    public const string QueryUnusable = "query-unusable";

    private static readonly string[] AllCodes =
    [
        ProviderUnknown,
        LookupUnsupported,
        LookupUnavailable,
        HistoryExhausted,
        PayloadUnreadable,
        QueryUnusable,
    ];

    /// <summary>Every code a lookup can refuse with, for exhaustive handling by callers.</summary>
    public static IReadOnlyList<string> All => AllCodes;

    /// <summary>
    /// Builds the failure for one catalogue code, so every adapter refuses the
    /// same reason the same way: an absent route is not found, an unreachable
    /// provider is an integration failure, and a query this provider cannot
    /// use is a validation failure.
    /// </summary>
    public static Result<T> Refuse<T>(string code) => code switch
    {
        ProviderUnknown or LookupUnsupported or HistoryExhausted => Result.NotFound<T>(code),
        LookupUnavailable or PayloadUnreadable => Result.IntegrationFailure<T>(code),
        QueryUnusable => Result.ValidationError<T>(code),
        _ => throw new ArgumentOutOfRangeException(
            nameof(code),
            code,
            "A provider lookup refusal must name one of the published catalogue codes."),
    };

    /// <summary>Tells whether a result is the refusal named by <paramref name="code"/>.</summary>
    public static bool Is<T>(Result<T> result, string code)
        => result.IsFailure && string.Equals(result.Error, code, StringComparison.Ordinal);
}
