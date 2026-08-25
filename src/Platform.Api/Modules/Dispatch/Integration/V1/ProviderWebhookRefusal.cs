using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.Dispatch.Integration.V1;

/// <summary>
/// Closed catalogue of reasons this module refuses an inbound provider
/// callback. A refusal is data, never an exception: the caller answers the
/// provider, decides whether to retry and decides which alarm to raise, and
/// none of those decisions survive a stack unwind.
/// <para>
/// The codes are stable because they are read by machines.
/// <see cref="OriginNotAllowed"/> in particular has to stay separable from
/// the others: a callback from an address outside the provider's published
/// range is an attempted forgery and deserves a security alarm, while an
/// invalid signature is also the everyday symptom of a rotated secret.
/// </para>
/// <para>
/// The failure carries the code as its whole error text, with no appended
/// detail. That is a security choice, not terseness: everything an adapter
/// could add at that point (the body, the destination, the signature that did
/// not match) is either personal data or attacker-supplied.
/// </para>
/// </summary>
public static class ProviderWebhookRefusal
{
    /// <summary>The presented signature does not prove the provider sent these bytes.</summary>
    public const string SignatureInvalid = "signature-invalid";

    /// <summary>
    /// The callback is authentic but too old or too far in the future to be
    /// accepted, which is how a captured callback is replayed.
    /// </summary>
    public const string TimestampOutOfWindow = "timestamp-out-of-window";

    /// <summary>The callback came from an address outside the configured allowlist.</summary>
    public const string OriginNotAllowed = "origin-not-allowed";

    /// <summary>The bytes do not form the shape the provider documents.</summary>
    public const string PayloadUnreadable = "payload-unreadable";

    /// <summary>No adapter in this process speaks for the named provider.</summary>
    public const string ProviderUnknown = "provider-unknown";

    private static readonly string[] AllCodes =
    [
        SignatureInvalid,
        TimestampOutOfWindow,
        OriginNotAllowed,
        PayloadUnreadable,
        ProviderUnknown,
    ];

    /// <summary>Every code this module can refuse with, for exhaustive handling by callers.</summary>
    public static IReadOnlyList<string> All => AllCodes;

    /// <summary>
    /// Builds the failure for one catalogue code. The error kind is derived
    /// here so that every adapter refuses the same reason the same way: the
    /// three authentication reasons are forbidden, an unreadable payload is a
    /// validation failure, and an unknown provider is a missing route.
    /// </summary>
    public static Result<T> Refuse<T>(string code) => code switch
    {
        SignatureInvalid or TimestampOutOfWindow or OriginNotAllowed => Result.Forbidden<T>(code),
        PayloadUnreadable => Result.ValidationError<T>(code),
        ProviderUnknown => Result.NotFound<T>(code),
        _ => throw new ArgumentOutOfRangeException(
            nameof(code),
            code,
            "A provider webhook refusal must name one of the published catalogue codes."),
    };

    /// <summary>
    /// Tells whether a result is the refusal named by <paramref name="code"/>.
    /// Callers use this instead of comparing error text by hand, because the
    /// guarantee that the error text is exactly the code belongs to this type.
    /// </summary>
    public static bool Is<T>(Result<T> result, string code)
        => result.IsFailure && string.Equals(result.Error, code, StringComparison.Ordinal);
}
