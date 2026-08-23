namespace NotificationHub.Api.Modules.Dispatch.Integration.V1;

/// <summary>
/// Normalized result of one provider call. <see cref="ErrorCode"/> carries
/// the provider's own stable code (or a normalized transport code such as
/// <c>timeout</c>); <see cref="ErrorMessage"/> carries the provider's text
/// after sanitization, because provider messages occasionally echo addresses
/// and the attempt record must stay free of personal data.
/// </summary>
public sealed record ProviderResult(
    ProviderOutcome Outcome,
    string? ProviderMessageId,
    string? ErrorCode,
    string? ErrorMessage,
    TimeSpan? RetryAfter)
{
    public static ProviderResult Accepted(string? providerMessageId)
        => new(ProviderOutcome.Accepted, providerMessageId, null, null, null);

    public static ProviderResult Rejected(string errorCode, string? errorMessage)
        => new(ProviderOutcome.Rejected, null, errorCode, errorMessage, null);

    public static ProviderResult Throttled(string errorCode, string? errorMessage, TimeSpan? retryAfter)
        => new(ProviderOutcome.Throttled, null, errorCode, errorMessage, retryAfter);

    public static ProviderResult Transient(string errorCode, string? errorMessage)
        => new(ProviderOutcome.TransientError, null, errorCode, errorMessage, null);
}
