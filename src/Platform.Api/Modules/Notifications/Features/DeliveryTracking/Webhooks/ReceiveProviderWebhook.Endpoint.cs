using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;
using NotificationHub.Api.Modules.Dispatch.Integration.V1;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Authentication;
using NotificationHub.Api.Modules.Notifications.Infrastructure.RateLimiting;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.Notifications.Features.DeliveryTracking.Webhooks;

/// <summary>
/// Inbound provider callback: the route that takes delivery feedback,
/// stores it as evidence and answers. Everything that decides what the
/// feedback means happens later, off the request, because the provider
/// measures this hub by how fast it answers and retries whatever takes too
/// long.
/// </summary>
internal static partial class ReceiveProviderWebhook
{
    /// <summary>What one callback produced, counted rather than described.</summary>
    internal sealed record Receipt(int Received, int Stored, int Duplicated);

    internal static void MapEndpoint(RouteGroupBuilder group)
        => group.MapPost("/{provider}", HandleHttpAsync)
            .RequireAuthorization(NotificationsProviderSignatureSetup.WebhookPolicyName)
            .RequireRateLimiting(NotificationsRateLimitingSetup.ProviderWebhookPolicyName)

            // The ceiling is applied per route and not per host, because this
            // is the one route whose body is written by somebody outside and
            // whose response time is measured by that same somebody. The
            // authentication scheme reads the whole body to verify it, so the
            // limit has to bind before the scheme runs, which is what the
            // feature does: it is read by the server when the body is first
            // touched.
            .AddEndpointFilter(LimitBodyAsync)
            .WithName("ReceiveProviderWebhook");

    /// <summary>
    /// Narrows the request body ceiling for this route.
    /// <para>
    /// A server that cannot be told, because the body is already buffered or
    /// the feature is missing, is left alone rather than refused: the host
    /// ceiling still applies, and refusing every callback over a knob nobody
    /// configured would be the worse failure of the two.
    /// </para>
    /// </summary>
    private static async ValueTask<object?> LimitBodyAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        IHttpMaxRequestBodySizeFeature? feature =
            context.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (feature is { IsReadOnly: false })
        {
            feature.MaxRequestBodySize = context.HttpContext.RequestServices
                .GetRequiredService<IOptions<ProviderWebhookIngestionOptions>>()
                .Value.MaxBodyBytes;
        }

        return await next(context);
    }

    /// <summary>
    /// The correlation identifiers ride in the callback URL this hub gave the
    /// provider, because one provider echoes nothing back in the body and its
    /// callback address is the only place left to carry them. They are a hint,
    /// never an authority: an event that carries its own correlation keeps it.
    /// </summary>
    private static async Task<IResult> HandleHttpAsync(
        HttpContext context,
        Handler handler,
        Guid? notificationId,
        Guid? attemptId,
        CancellationToken cancellationToken)
    {
        VerifiedProviderWebhook? verified = ProviderSignatureDefaults.FindVerifiedWebhook(context);
        if (verified is null)
        {
            // The authentication scheme leaves the proven bytes behind on
            // every success, so their absence here is a composition defect,
            // not untrusted input to fall back on.
            return Results.Problem(statusCode: StatusCodes.Status500InternalServerError);
        }

        Result<Receipt> result = await handler.HandleAsync(
            new Command(verified, RouteCorrelation(notificationId, attemptId)),
            cancellationToken);

        return result.IsSuccess
            ? Results.Accepted()
            : Problem(result);
    }

    private static DispatchCorrelation? RouteCorrelation(Guid? notificationId, Guid? attemptId)
        => notificationId is { } notification && attemptId is { } attempt
            ? new DispatchCorrelation(notification, attempt)
            : null;

    private static IResult Problem(Result<Receipt> result)
    {
        var code = result.Error ?? ProviderWebhookRefusal.PayloadUnreadable;
        var status = code switch
        {
            // A batch over the ceiling is answered by its own status, so an
            // operator reading the access log can tell it apart from a payload
            // this hub could not parse. The two are different problems: one is
            // a provider or an attacker sending more than this route accepts,
            // the other is an adapter that no longer agrees with its provider.
            DeliveryWebhookRefusal.BatchTooLarge => StatusCodes.Status413PayloadTooLarge,
            _ => result.ErrorKind == ResultErrorKind.NotFound
                ? StatusCodes.Status404NotFound
                : StatusCodes.Status400BadRequest,
        };
        return Results.Problem(statusCode: status, title: code, type: code);
    }
}
