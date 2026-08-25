using Microsoft.AspNetCore.Authentication;

namespace NotificationHub.Api.Modules.Notifications.Infrastructure.Authentication;

/// <summary>
/// Knobs of the provider-signature scheme. Nothing here is required at host
/// start: a host without provider callbacks boots with the section absent and
/// verifies against the request as it arrived.
/// </summary>
public sealed class ProviderSignatureOptions : AuthenticationSchemeOptions
{
    /// <summary>
    /// Public base address callbacks are addressed to, for example
    /// <c>https://hooks.example.com</c>. It exists because one provider folds
    /// the full request URL, query string included, into the signed payload,
    /// while behind a load balancer the address this process observes is the
    /// internal one. Signing over the internal address would refuse every
    /// authentic callback in production and still pass every test, since a
    /// test host observes the address it was called on. Empty keeps the
    /// address of the request, which is correct wherever no proxy rewrites it.
    /// </summary>
    public string? PublicBaseUrl { get; set; }

    /// <summary>
    /// Largest callback body this scheme reads into memory before refusing.
    /// The body must be buffered whole, because every scheme verified here
    /// signs the exact octets, so the ceiling is what keeps an unauthenticated
    /// caller from choosing this process's memory footprint.
    /// </summary>
    public int MaxBodyBytes { get; set; } = 1_048_576;

    /// <inheritdoc />
    public override void Validate()
    {
        base.Validate();
        if (MaxBodyBytes <= 0)
        {
            throw new InvalidOperationException(
                "O limite de corpo do esquema de assinatura de provedor precisa ser positivo.");
        }

        if (!string.IsNullOrWhiteSpace(PublicBaseUrl)
            && !Uri.TryCreate(PublicBaseUrl, UriKind.Absolute, out _))
        {
            throw new InvalidOperationException(
                "A base pública dos webhooks de provedor precisa ser uma URL absoluta.");
        }
    }
}
