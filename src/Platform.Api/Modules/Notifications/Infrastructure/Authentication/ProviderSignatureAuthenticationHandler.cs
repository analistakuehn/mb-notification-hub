using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using NotificationHub.Api.Modules.Dispatch.Integration.V1;
using NotificationHub.Api.Modules.Notifications.Features.DeliveryTracking.Webhooks;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.Notifications.Infrastructure.Authentication;

/// <summary>
/// Authenticates an inbound provider callback by the signature the provider
/// computed over the exact bytes it sent. The scheme exists so the webhook
/// route is an authenticated route like every other state-changing route of
/// this host, instead of an anonymous one carrying its own verification: the
/// proof runs once, before the endpoint, and what reaches the endpoint is
/// bytes already proven.
/// <para>
/// The body is buffered and rewound rather than read through a binder: every
/// signature scheme verified here signs the precise octets, and a round trip
/// through a parsed model re-encodes them and invalidates the proof.
/// </para>
/// </summary>
internal sealed class ProviderSignatureAuthenticationHandler(
    IOptionsMonitor<ProviderSignatureOptions> options,
    ILoggerFactory loggerFactory,
    UrlEncoder encoder,
    IProviderWebhookInterpreterResolver resolver,
    IOptions<ProviderWebhookIngestionOptions> ingestionOptions)
    : AuthenticationHandler<ProviderSignatureOptions>(options, loggerFactory, encoder)
{
    private const int CopyChunkBytes = 8 * 1024;

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.RouteValues.TryGetValue(
                ProviderSignatureDefaults.ProviderRouteValue, out var routeValue)
            || routeValue is not string providerKey
            || string.IsNullOrWhiteSpace(providerKey))
        {
            // The scheme only speaks for the published webhook route. Anywhere
            // else it has nothing to say, which is not the same as a refusal.
            return AuthenticateResult.NoResult();
        }

        Result<IProviderWebhookInterpreter> interpreter = resolver.Resolve(providerKey);
        if (interpreter.IsFailure)
        {
            Logger.ProviderWebhookProviderUnknown(providerKey);
            return AuthenticateResult.Fail(ProviderWebhookRefusal.ProviderUnknown);
        }

        ReadOnlyMemory<byte>? body = await TryReadBodyAsync();
        if (body is null)
        {
            Logger.ProviderWebhookBodyTooLarge(providerKey, ingestionOptions.Value.MaxBodyBytes);
            return AuthenticateResult.Fail(ProviderWebhookRefusal.PayloadUnreadable);
        }

        Result<VerifiedProviderWebhook> verified = interpreter.Value!.Verify(new ProviderWebhookRequest(
            providerKey,
            ResolveSignedUrl(),
            ReadHeaders(),
            Context.Connection.RemoteIpAddress?.ToString(),
            body.Value));
        if (verified.IsFailure)
        {
            var refusal = verified.Error ?? ProviderWebhookRefusal.SignatureInvalid;
            if (string.Equals(refusal, ProviderWebhookRefusal.OriginNotAllowed, StringComparison.Ordinal))
            {
                // Its own event on purpose: an address outside the provider's
                // published range is an attempted forgery and deserves an
                // alarm, while an invalid signature is also the everyday
                // symptom of a rotated secret.
                Logger.ProviderWebhookOriginRejected(providerKey);
            }
            else
            {
                Logger.ProviderWebhookRefused(providerKey, refusal);
            }

            return AuthenticateResult.Fail(refusal);
        }

        // The endpoint reads the proven bytes from here instead of the body:
        // holding this instance is the claim that they came from the named
        // provider, and re-reading the request would drop that claim.
        Context.Items[ProviderSignatureDefaults.VerifiedWebhookItemKey] = verified.Value!;
        Logger.ProviderWebhookVerified(providerKey);

        var identity = new ClaimsIdentity(
            [new Claim(ProviderSignatureDefaults.ProviderKeyClaimType, verified.Value!.ProviderKey)],
            Scheme.Name);
        return AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }

    /// <summary>
    /// The URL the provider signed. Taken from the configured public base
    /// whenever there is one, because behind a load balancer the address this
    /// process observes is the internal one while the provider signed the
    /// public one.
    /// </summary>
    private string ResolveSignedUrl()
    {
        var configured = Options.PublicBaseUrl;
        return string.IsNullOrWhiteSpace(configured)
            ? Request.GetDisplayUrl()
            : string.Concat(
                configured.TrimEnd('/'),
                Request.PathBase.Value,
                Request.Path.Value,
                Request.QueryString.Value);
    }

    private Dictionary<string, string> ReadHeaders()
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, StringValues> header in Request.Headers) headers[header.Key] = header.Value.ToString();

        return headers;
    }

    /// <summary>
    /// Buffers the request body and rewinds it, so the proof reads the exact
    /// octets and whatever runs later still sees an unread stream. Returns
    /// null when the body exceeds the configured ceiling, which is a refusal
    /// rather than an allocation.
    /// </summary>
    private async Task<ReadOnlyMemory<byte>?> TryReadBodyAsync()
    {
        // One ceiling on the body, owned by the ingestion options and applied
        // here as well because this scheme buffers the whole body to prove the
        // signature over the exact octets.
        var maxBytes = ingestionOptions.Value.MaxBodyBytes;
        if (Request.ContentLength is { } declared && declared > maxBytes) return null;

        Request.EnableBuffering();
        Request.Body.Position = 0;

        using var buffer = new MemoryStream();
        var chunk = new byte[CopyChunkBytes];
        int read;
        while ((read = await Request.Body.ReadAsync(chunk, Context.RequestAborted)) > 0)
        {
            if (buffer.Length + read > maxBytes)
            {
                Request.Body.Position = 0;
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        Request.Body.Position = 0;
        return buffer.ToArray();
    }
}
