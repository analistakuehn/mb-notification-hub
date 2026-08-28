namespace NotificationHub.Api.Modules.ContactConsent.Features.Recipients;

internal static partial class DeclareContactPoints
{
    /// <summary>
    /// The declared set of contact points of one recipient. The declaration is
    /// the whole truth for contact points: a value absent from the list stops
    /// being addressable. The profile preferences ride along and apply only
    /// when present, because this route owns the contact declaration, not the
    /// profile's full state.
    /// </summary>
    internal sealed record Command(IReadOnlyList<ContactPointDeclaration> ContactPoints)
    {
        /// <summary>IANA timezone preference; applied only when present.</summary>
        public string? Timezone { get; init; }

        /// <summary>Locale preference; applied only when present.</summary>
        public string? Locale { get; init; }
    }

    /// <summary>One declared contact value on one channel.</summary>
    internal sealed record ContactPointDeclaration(string Channel, string Value, bool Verified);
}
