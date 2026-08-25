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
            .WithName("ReceiveProviderWebhook");

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
        var status = result.ErrorKind == ResultErrorKind.NotFound
            ? StatusCodes.Status404NotFound
            : StatusCodes.Status400BadRequest;
        return Results.Problem(statusCode: status, title: code, type: code);
    }
}
