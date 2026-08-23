using NotificationHub.Api.Modules.TemplateManagement.Integration.V1;

namespace NotificationHub.Api.Modules.Notifications.Features.Mutations;

internal static partial class RequestNotification
{
    /// <summary>Body of both the 202 acceptance and the 200 idempotent replay.</summary>
    internal sealed record Response(string NotificationId, string Status);

    /// <summary>
    /// Every outcome of the use case, modeled as data: rejections are
    /// legitimate flows of the ingestion, not errors, and each one maps to
    /// exactly one protocol response at the endpoint.
    /// </summary>
    internal abstract record Outcome
    {
        private Outcome()
        {
        }

        /// <summary>Everything committed; answer 202 with the public id.</summary>
        internal sealed record Accepted(Guid NotificationId) : Outcome;

        /// <summary>Idempotent replay of an earlier acceptance; answer 200 with the same id.</summary>
        internal sealed record Replayed(Guid NotificationId) : Outcome;

        /// <summary>Same idempotency key, different payload; answer 409.</summary>
        internal sealed record IdempotencyConflict : Outcome;

        /// <summary>The producer token carries no role covering the requested class; answer 403.</summary>
        internal sealed record ClassNotAllowed(string CanonicalClass) : Outcome;

        /// <summary>The published catalog rejected the request; answer 422 with the stable reason.</summary>
        internal sealed record TemplateRejected(
            string Reason,
            string Detail,
            IReadOnlyList<VariablesValidationCheck>? Checks) : Outcome;

        /// <summary>A rate limit rejected the request; answer 429 with the retry hint.</summary>
        internal sealed record RateLimited(int RetryAfterSeconds) : Outcome;
    }
}
