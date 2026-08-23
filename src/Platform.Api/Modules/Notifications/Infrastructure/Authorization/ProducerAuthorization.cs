using NotificationHub.Api.Modules.Notifications.Domain;
using NotificationHub.Api.Modules.Notifications.Integration.V1;

namespace NotificationHub.Api.Modules.Notifications.Infrastructure.Authorization;

/// <summary>
/// An authorization question already answered. The ingestion service receives
/// the verdict, never the evidence: app roles belong to the REST transport and
/// the producer registry belongs to the bus, and a use case that knew both
/// would have to grow a branch per transport for a decision neither of them
/// is about.
/// </summary>
internal abstract record ProducerAuthorization
{
    private ProducerAuthorization()
    {
    }

    /// <summary>The producer may request this class for this application.</summary>
    internal sealed record Allowed : ProducerAuthorization;

    /// <summary>
    /// The producer may not; <see cref="Reason"/> is the canonical catalog
    /// value of the transport that answered.
    /// </summary>
    internal sealed record Denied(string Reason) : ProducerAuthorization;
}

/// <summary>
/// Authorizes a REST producer from the app roles of its Entra token. The class
/// travels in the body, so the check runs against the resource, never against
/// the route.
/// </summary>
internal static class RestProducerAuthorizer
{
    public static ProducerAuthorization Authorize(IReadOnlySet<string> producerRoles, string canonicalClass)
    {
        ArgumentNullException.ThrowIfNull(producerRoles);

        // A class outside the vocabulary can only reach here ahead of the
        // shape validation that rejects it; denying keeps this method total,
        // and the invalid payload still wins because the service validates
        // before it consults this verdict.
        if (!NotificationClasses.IsCanonical(canonicalClass))
        {
            return new ProducerAuthorization.Denied(
                NotificationRejectionReasons.ClassNotAllowedForPrincipal);
        }

        return producerRoles.Contains(NotificationClasses.RequiredRole(canonicalClass))
            ? new ProducerAuthorization.Allowed()
            : new ProducerAuthorization.Denied(NotificationRejectionReasons.ClassNotAllowedForPrincipal);
    }
}

/// <summary>
/// Authorizes a bus producer against the materialized registry. An empty or
/// unreadable registry is an operational failure, never a mass denial: the
/// caller must stop consuming instead of sending a day of legitimate traffic
/// to the dead-letter topic.
/// </summary>
internal sealed class KafkaProducerAuthorizer(IProducerRegistry registry)
{
    public async Task<ProducerAuthorization> AuthorizeAsync(
        string principal,
        string application,
        string canonicalClass,
        CancellationToken cancellationToken)
    {
        ProducerGrants grants = await registry.CurrentAsync(cancellationToken)
            ?? throw new InvalidOperationException(
                "O registro de produtores está indisponível; a entrada pelo barramento não autoriza sem ele.");
        if (grants.IsEmpty)
        {
            throw new InvalidOperationException(
                "O registro de produtores está vazio; tabela vazia não se distingue de materialização que não rodou.");
        }

        return grants.Allows(principal, application, canonicalClass)
            ? new ProducerAuthorization.Allowed()
            : new ProducerAuthorization.Denied(NotificationRejectionReasons.ProducerNotAuthorized);
    }
}
