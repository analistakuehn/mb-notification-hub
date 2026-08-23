using NotificationHub.Api.Modules.Notifications.Infrastructure.Idempotency;

namespace NotificationHub.Api.Modules.Notifications.Infrastructure.RateLimiting;

/// <summary>
/// The two Redis-backed controls the ingestion consults before it opens a
/// transaction: the idempotency fast path and the rate limiter. They travel
/// together because they share one posture: both are advisory, both fail open
/// with an alarm, and neither is the authority for what it protects (the
/// unique key of the idempotency table is, and the kill switch is). Bundling
/// them keeps the use case reading as a sequence of decisions instead of a
/// list of stores.
/// </summary>
internal sealed class IngressControls(
    IdempotencyFastPath idempotency,
    IngestionRateLimiter rateLimiter)
{
    /// <summary>The remembered acceptance of an obvious replay, or null on a miss.</summary>
    public Task<RememberedAcceptance?> FindRememberedAsync(
        string application,
        string idempotencyKey,
        CancellationToken cancellationToken)
        => idempotency.FindAsync(application, idempotencyKey, cancellationToken);

    /// <summary>Remembers an acceptance; called strictly after the database commit.</summary>
    public Task RememberAsync(
        string application,
        string idempotencyKey,
        RememberedAcceptance acceptance,
        CancellationToken cancellationToken)
        => idempotency.RememberAsync(application, idempotencyKey, acceptance, cancellationToken);

    /// <summary>Evaluates both rate-limit dimensions for one request.</summary>
    public Task<RateLimitDecision> EvaluateRateLimitAsync(
        RateLimitSubject subject,
        bool enforcePrincipalLimit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subject);
        return rateLimiter.EvaluateAsync(
            subject.Principal,
            subject.Application,
            subject.RecipientId,
            subject.CanonicalClass,
            enforcePrincipalLimit,
            cancellationToken);
    }
}

/// <summary>Who and what one rate-limit evaluation is about.</summary>
internal sealed record RateLimitSubject(
    string Principal,
    string Application,
    string RecipientId,
    string CanonicalClass);
