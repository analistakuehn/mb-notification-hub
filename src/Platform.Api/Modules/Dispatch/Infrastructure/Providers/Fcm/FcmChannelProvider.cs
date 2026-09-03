using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;
using NotificationHub.Api.Modules.Dispatch.Integration.V1;
using NotificationHub.Api.Modules.TemplateManagement.Integration.V1;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace NotificationHub.Api.Modules.Dispatch.Infrastructure.Providers.Fcm;

/// <summary>
/// HTTP v1 adapter. <c>UNREGISTERED</c> and <c>INVALID_ARGUMENT</c> surface
/// as permanent rejections carrying the provider code, because the caller
/// invalidates the device token on exactly those codes; quota exhaustion is
/// throttling; everything without a provider verdict stays transient. The
/// adapter never retries a send; only the idempotent token acquisition
/// retries, on its own client.
/// </summary>
internal sealed class FcmChannelProvider(
    IHttpClientFactory httpClientFactory,
    FcmAccessTokenSource tokenSource,
    IOptions<FcmOptions> options,
    ILogger<FcmChannelProvider> logger) : IChannelProvider
{
    internal const string Key = "fcm";
    internal const string HttpClientName = "dispatch-fcm";

    private static readonly string[] PermanentRejectionCodes =
    [
        "UNREGISTERED",
        "INVALID_ARGUMENT",
        "SENDER_ID_MISMATCH",
        "THIRD_PARTY_AUTH_ERROR",
    ];

    public Channel Channel => Channel.Push;

    public string ProviderKey => Key;

    /// <summary>
    /// No: the message this adapter builds carries a notification body and a
    /// small data payload, and neither is a place a document can travel in.
    /// The route that plans the send refuses the plan instead of sending a set
    /// nobody would receive.
    /// </summary>
    public bool CarriesAttachments => false;

    public async Task<ProviderResult> SendAsync(DispatchRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        (PushDeliveryTarget target, PushMessage message) = Discriminate(request);
        FcmOptions config = options.Value;
        EnsureConfigured(config);

        string accessToken;
        try
        {
            accessToken = await tokenSource.GetAccessTokenAsync(cancellationToken);
        }
        catch (FcmTokenUnavailableException exception)
        {
            var reason = ProviderErrorSanitizer.Sanitize(exception.Message);
            return ProviderResult.Transient("auth-token", reason);
        }

        FcmSendRequest payload = BuildRequest(target, message);
        HttpClient client = httpClientFactory.CreateClient(HttpClientName);
        using var httpRequest = new HttpRequestMessage(
            HttpMethod.Post, $"/v1/projects/{config.ProjectId}/messages:send");
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        httpRequest.Content = JsonContent.Create(payload);

        try
        {
            using HttpResponseMessage response = await client.SendAsync(httpRequest, cancellationToken);
            return await MapAsync(response, cancellationToken);
        }
        catch (BrokenCircuitException)
        {
            logger.FcmCircuitOpen();
            return ProviderResult.Transient("circuit-open", null);
        }
        catch (TimeoutRejectedException)
        {
            logger.FcmTimedOut(config.TimeoutSeconds);
            return ProviderResult.Transient("timeout", null);
        }
        catch (HttpRequestException exception)
        {
            logger.FcmNetworkFault(exception);
            return ProviderResult.Transient("network", ProviderErrorSanitizer.Sanitize(exception.Message));
        }
    }

    internal static FcmSendRequest BuildRequest(PushDeliveryTarget target, PushMessage message)
        => new(new FcmMessage(
            target.DeviceToken,
            new FcmNotification(message.Title, message.Body),
            message.DataPayload.Count == 0 ? null : message.DataPayload));

    private async Task<ProviderResult> MapAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var statusCode = (int)response.StatusCode;

        if (response.IsSuccessStatusCode)
        {
            FcmSendResponse? accepted = null;
            try
            {
                accepted = await response.Content.ReadFromJsonAsync<FcmSendResponse>(cancellationToken);
            }
            catch (JsonException)
            {
                // Acceptance without a readable body still counts: the
                // provider took the message; only the id is missing.
            }

            logger.FcmAccepted(statusCode);
            return ProviderResult.Accepted(accepted?.Name);
        }

        (var errorCode, var errorMessage) = await ReadErrorAsync(response, cancellationToken);
        logger.FcmSendFailed(statusCode, errorCode);

        if (PermanentRejectionCodes.Contains(errorCode, StringComparer.Ordinal))
        {
            return ProviderResult.Rejected(errorCode, errorMessage);
        }

        if (response.StatusCode is HttpStatusCode.TooManyRequests
            || string.Equals(errorCode, "QUOTA_EXCEEDED", StringComparison.Ordinal)
            || string.Equals(errorCode, "RESOURCE_EXHAUSTED", StringComparison.Ordinal))
        {
            return ProviderResult.Throttled(errorCode, errorMessage, response.Headers.RetryAfter?.Delta);
        }

        // Remaining verdicts (5xx, UNAVAILABLE, INTERNAL, credential faults)
        // stay transient: none of them condemns this message permanently.
        return ProviderResult.Transient(errorCode, errorMessage);
    }

    private static async Task<(string ErrorCode, string? ErrorMessage)> ReadErrorAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var fallbackCode = $"http-{(int)response.StatusCode}";
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(body))
        {
            return (fallbackCode, null);
        }

        try
        {
            FcmErrorBody? error = JsonSerializer.Deserialize<FcmErrorResponse>(body)?.Error;
            if (error is null)
            {
                return (fallbackCode, ProviderErrorSanitizer.Sanitize(body));
            }

            // The v1 error carries the specific code in the FcmError detail;
            // the RPC status is the coarser fallback.
            var detailCode = error.Details?
                .FirstOrDefault(detail => !string.IsNullOrWhiteSpace(detail.ErrorCode))?
                .ErrorCode;
            var errorCode = detailCode
                ?? (string.IsNullOrWhiteSpace(error.Status) ? fallbackCode : error.Status);
            return (errorCode, ProviderErrorSanitizer.Sanitize(error.Message));
        }
        catch (JsonException)
        {
            return (fallbackCode, ProviderErrorSanitizer.Sanitize(body));
        }
    }

    private static (PushDeliveryTarget Target, PushMessage Message) Discriminate(DispatchRequest request)
    {
        if (request.Target is not PushDeliveryTarget target || request.Message is not PushMessage message)
        {
            throw new InvalidOperationException(
                "The FCM adapter delivers push only; it received a request for another channel.");
        }

        return (target, message);
    }

    private static void EnsureConfigured(FcmOptions config)
    {
        if (string.IsNullOrWhiteSpace(config.ProjectId))
        {
            throw new InvalidOperationException(
                $"Missing configuration '{FcmOptions.SectionName}:ProjectId'.");
        }
    }
}
