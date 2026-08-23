namespace NotificationHub.Api.Modules.ContactConsent.Features.Mutations;

internal static partial class DeclareContactPoints
{
    /// <summary>
    /// The resulting declared state: profile preferences with the default
    /// timezone applied and the active contact points, addressable by id.
    /// Contact values never appear in responses.
    /// </summary>
    internal sealed record Response(
        string RecipientId,
        string Timezone,
        string? Locale,
        IReadOnlyList<ContactPointItem> ContactPoints);

    internal sealed record ContactPointItem(Guid ContactPointId, string Channel, bool Verified);

    /// <summary>Every outcome of the use case, modeled as data.</summary>
    internal abstract record Outcome
    {
        private Outcome()
        {
        }

        /// <summary>Everything committed (or the declaration matched the current state); answer 200.</summary>
        internal sealed record Declared(Response Response) : Outcome;

        /// <summary>A concurrent declaration for the same recipient won the race; answer 409.</summary>
        internal sealed record ConcurrencyConflict : Outcome;

        /// <summary>
        /// The record that carried the declaration was already settled and
        /// nothing was written again. Only a transport that deduplicates by
        /// record reaches it; the REST route has no record and never does.
        /// </summary>
        internal sealed record Duplicate : Outcome;
    }
}
