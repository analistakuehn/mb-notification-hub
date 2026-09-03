using NotificationHub.Api.Modules.Notifications.Infrastructure.RateLimiting;
using NotificationHub.Api.Modules.TemplateManagement.Integration.V1;

namespace NotificationHub.Api.Modules.Notifications.Features.Ingress.RequestNotification;

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

        /// <summary>The emergency control disabled this producer; answer 403.</summary>
        internal sealed record ProducerDisabled : Outcome;

        /// <summary>The emergency-control authority is unavailable; answer 503.</summary>
        internal sealed record KillSwitchUnavailable : Outcome;

        /// <summary>The published catalog rejected the request; answer 422 with the stable reason.</summary>
        internal sealed record TemplateRejected(
            string Reason,
            string Detail,
            IReadOnlyList<VariablesValidationCheck>? Checks) : Outcome;

        /// <summary>
        /// A rate limit rejected the request; answer 429 with the retry hint.
        /// The dimension travels with the outcome because the two ask the
        /// producer for opposite behaviors: an exhausted recipient budget means
        /// the customer is protected and the request must not be retried, while
        /// an exhausted principal budget means slow down and retry.
        /// </summary>
        internal sealed record RateLimited(RateLimitedDimension Dimension, int RetryAfterSeconds) : Outcome;

        /// <summary>
        /// The request failed the shape validation, on either transport. The
        /// use case owns this refusal so the same defect gets the same reason,
        /// the same trail and the same rejection event whichever transport
        /// carried it; the field errors travel so the synchronous caller keeps
        /// its per-field report and the dead-letter record keeps its diagnosis.
        /// </summary>
        internal sealed record PayloadInvalid(IReadOnlyDictionary<string, string[]> Errors) : Outcome;

        /// <summary>
        /// The producer may not request this class for this application;
        /// <see cref="Reason"/> is the canonical reason of the transport that
        /// answered the authorization question.
        /// </summary>
        internal sealed record ProducerNotAuthorized(string Reason) : Outcome;

        /// <summary>
        /// The template declares sensitive variables and the request arrived
        /// over the shared bus. The names are carried so the dead-letter
        /// record can say which variables the template declares without ever
        /// carrying a value.
        /// </summary>
        internal sealed record SensitiveVariablesOnBus(IReadOnlyList<string> VariableNames) : Outcome;

        /// <summary>
        /// The attachments the request named could not be claimed, so no
        /// notification was created; answer 422.
        /// <para>
        /// It carries nothing about which member refused or why. The
        /// difference between an attachment nobody minted and one that belongs
        /// to another application is not something a refusal may reveal, and
        /// the difference between one that was never released and one whose
        /// release was taken back is a reading of the lifecycle rather than an
        /// answer to a request.
        /// </para>
        /// </summary>
        internal sealed record AttachmentsNotClaimable : Outcome;
    }
}
