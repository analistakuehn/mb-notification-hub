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

        /// <summary>The published catalog rejected the request; answer 422 with the stable reason.</summary>
        internal sealed record TemplateRejected(
            string Reason,
            string Detail,
            IReadOnlyList<VariablesValidationCheck>? Checks) : Outcome;

        /// <summary>A rate limit rejected the request; answer 429 with the retry hint.</summary>
        internal sealed record RateLimited(int RetryAfterSeconds) : Outcome;

        /// <summary>
        /// The request failed the shape validation. Unreachable over REST,
        /// where the endpoint filter answers the published 400 first; the bus
        /// path needs it as data, because a malformed event has no caller to
        /// answer and must reach the dead-letter topic with its field errors.
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
    }
}
