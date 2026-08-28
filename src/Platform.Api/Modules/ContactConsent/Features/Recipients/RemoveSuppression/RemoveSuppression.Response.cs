namespace NotificationHub.Api.Modules.ContactConsent.Features.Recipients;

internal static partial class RemoveSuppression
{
    /// <summary>
    /// State of the reversal. The address never echoes back: the caller
    /// already named the row it wanted, and answering with the value would
    /// turn an administrative route into a way to read contact data.
    /// </summary>
    internal sealed record Response(
        Guid ContactPointId,
        string Channel,
        string Reason,
        DateTimeOffset SuppressedAt,
        DateTimeOffset RemovedAt);

    /// <summary>Every outcome of the use case, modeled as data.</summary>
    internal abstract record Outcome
    {
        private Outcome()
        {
        }

        /// <summary>The suppression in force was taken back; answer 200.</summary>
        internal sealed record Removed(Response Response) : Outcome;

        /// <summary>
        /// Nothing was in force for this contact point. A declarative no-op:
        /// the desired state already holds, so the answer is 200 with its own
        /// trail, not an error.
        /// </summary>
        internal sealed record NotSuppressed : Outcome;

        /// <summary>The contact point is unknown or belongs to another recipient; answer 404.</summary>
        internal sealed record ContactPointNotFound : Outcome;

        /// <summary>A concurrent write for the same recipient won the race; answer 409.</summary>
        internal sealed record ConcurrencyConflict : Outcome;
    }
}
