namespace NotificationHub.Api.Modules.ContactConsent.Features.Mutations;

internal static partial class DeclareConsents
{
    /// <summary>The consent state in force per (purpose, channel) after the declaration.</summary>
    internal sealed record Response(string RecipientId, IReadOnlyList<ConsentItem> Consents);

    internal sealed record ConsentItem(
        string Purpose,
        string Channel,
        bool Granted,
        string Source,
        string TermsVersion,
        DateTimeOffset RecordedAt);

    /// <summary>Every outcome of the use case, modeled as data.</summary>
    internal abstract record Outcome
    {
        private Outcome()
        {
        }

        /// <summary>Every difference recorded (or nothing differed); answer 200 with the state in force.</summary>
        internal sealed record Declared(Response Response) : Outcome;

        /// <summary>The recipient has no contact registration at all; answer 404.</summary>
        internal sealed record RecipientUnknown : Outcome;

        /// <summary>A declared channel has no active contact point to anchor the record; answer 422.</summary>
        internal sealed record NoContactPointForChannel(string Channel) : Outcome;

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
