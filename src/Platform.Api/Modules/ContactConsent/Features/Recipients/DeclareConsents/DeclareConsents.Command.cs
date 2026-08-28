namespace NotificationHub.Api.Modules.ContactConsent.Features.Recipients;

internal static partial class DeclareConsents
{
    /// <summary>
    /// The desired consent state per (purpose, channel). The module compares
    /// each entry with the state currently in force and only a difference
    /// produces a new ledger record; an identical declaration is an idempotent
    /// no-op that answers with the state in force.
    /// </summary>
    internal sealed record Command(IReadOnlyList<ConsentDeclaration> Consents);

    /// <summary>One desired consent state, with the evidence a record must carry.</summary>
    internal sealed record ConsentDeclaration(
        string Purpose,
        string Channel,
        bool Granted,
        string Source,
        string TermsVersion);
}
