using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;
using NotificationHub.Api.Modules.Dispatch.Integration.V1;
using NotificationHub.Api.Modules.TemplateManagement.Integration.V1;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace NotificationHub.Api.Modules.Dispatch.Infrastructure.Providers.SendGrid;

/// <summary>
/// Mail Send v3 adapter. Maps the provider verdict without optimism: only an
/// explicit acceptance is <see cref="ProviderOutcome.Accepted"/>; a 4xx is a
/// permanent rejection except 429, which is throttling; 5xx, timeout,
/// network fault and open circuit stay transient because the provider gave
/// no verdict. The adapter never retries a send: a mail send is not
/// idempotent at the provider, so redelivery belongs to the queue.
/// </summary>
internal sealed class SendGridChannelProvider(
    IHttpClientFactory httpClientFactory,
    IOptions<SendGridOptions> options,
    ILogger<SendGridChannelProvider> logger) : IChannelProvider
{
    internal const string Key = "sendgrid";
    internal const string HttpClientName = "dispatch-sendgrid";
    private const string MessageIdHeader = "X-Message-Id";

    public Channel Channel => Channel.Email;

    public string ProviderKey => Key;

    public async Task<ProviderResult> SendAsync(DispatchRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        (EmailDeliveryTarget target, EmailMessage message) = Discriminate(request);
        SendGridOptions config = options.Value;
        EnsureConfigured(config);

        SendGridMailRequest payload = BuildRequest(target, message, config, request.Correlation);
        HttpClient client = httpClientFactory.CreateClient(HttpClientName);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/v3/mail/send");
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.ApiKey);
        httpRequest.Content = JsonContent.Create(payload);

        try
        {
            using HttpResponseMessage response = await client.SendAsync(httpRequest, cancellationToken);
            return await MapAsync(response, cancellationToken);
        }
        catch (BrokenCircuitException)
        {
            logger.SendGridCircuitOpen();
            return ProviderResult.Transient("circuit-open", null);
        }
        catch (TimeoutRejectedException)
        {
            logger.SendGridTimedOut(config.TimeoutSeconds);
            return ProviderResult.Transient("timeout", null);
        }
        catch (HttpRequestException exception)
        {
            logger.SendGridNetworkFault(exception);
            return ProviderResult.Transient("network", ProviderErrorSanitizer.Sanitize(exception.Message));
        }
    }

    internal static SendGridMailRequest BuildRequest(
        EmailDeliveryTarget target,
        EmailMessage message,
        SendGridOptions config,
        DispatchCorrelation? correlation = null)
        => new(
            // custom_args carries the correlation ids the Event Webhook echoes
            // back; a pure pass-through that never touches the content bytes.
            [new SendGridPersonalization(
                [new SendGridAddress(target.EmailAddress, null)],
                correlation is null
                    ? null
                    : new Dictionary<string, string>
                    {
                        ["notification_id"] = correlation.NotificationId.ToString(),
                        ["attempt_id"] = correlation.AttemptId.ToString(),
                    })],
            new SendGridAddress(
                config.SenderEmail,
                string.IsNullOrWhiteSpace(config.SenderName) ? null : config.SenderName),
            message.Subject,
            // text/plain before text/html: Mail Send v3 requires content
            // ordered by ascending preference.
            [
                new SendGridContent("text/plain", message.TextBody),
                new SendGridContent("text/html", message.HtmlBody),
            ],
            new SendGridMailSettings(new SendGridSandboxMode(config.SandboxMode)));

    private async Task<ProviderResult> MapAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var statusCode = (int)response.StatusCode;

        // 202 is the live acceptance; sandbox-mode validation answers 200.
        if (response.StatusCode is HttpStatusCode.Accepted or HttpStatusCode.OK)
        {
            logger.SendGridAccepted(statusCode);
            return ProviderResult.Accepted(ReadMessageId(response));
        }

        var errorCode = $"http-{statusCode}";
        var errorMessage = await ReadErrorMessageAsync(response, cancellationToken);
        logger.SendGridSendFailed(statusCode, errorCode);

        if (response.StatusCode is HttpStatusCode.TooManyRequests)
        {
            return ProviderResult.Throttled(errorCode, errorMessage, response.Headers.RetryAfter?.Delta);
        }

        return statusCode is >= 400 and < 500
            ? ProviderResult.Rejected(errorCode, errorMessage)
            : ProviderResult.Transient(errorCode, errorMessage);
    }

    private static (EmailDeliveryTarget Target, EmailMessage Message) Discriminate(DispatchRequest request)
    {
        if (request.Target is not EmailDeliveryTarget target || request.Message is not EmailMessage message)
        {
            throw new InvalidOperationException(
                "The SendGrid adapter delivers e-mail only; it received a request for another channel.");
        }

        return (target, message);
    }

    private static void EnsureConfigured(SendGridOptions config)
    {
        if (string.IsNullOrWhiteSpace(config.ApiKey))
        {
            throw new InvalidOperationException(
                $"Missing configuration '{SendGridOptions.SectionName}:ApiKey'.");
        }

        if (string.IsNullOrWhiteSpace(config.SenderEmail))
        {
            throw new InvalidOperationException(
                $"Missing configuration '{SendGridOptions.SectionName}:SenderEmail'.");
        }
    }

    private static string? ReadMessageId(HttpResponseMessage response)
        => response.Headers.TryGetValues(MessageIdHeader, out IEnumerable<string>? values)
            ? values.FirstOrDefault()
            : null;

    private static async Task<string?> ReadErrorMessageAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            SendGridErrorResponse? parsed = JsonSerializer.Deserialize<SendGridErrorResponse>(body);
            if (parsed?.Errors is not { Count: > 0 } errors)
            {
                return ProviderErrorSanitizer.Sanitize(body);
            }

            SendGridError first = errors[0];

            var text = string.IsNullOrWhiteSpace(first.Field)
                ? first.Message
                : $"{first.Field}: {first.Message}";
            return ProviderErrorSanitizer.Sanitize(text);
        }
        catch (JsonException)
        {
            return ProviderErrorSanitizer.Sanitize(body);
        }
    }
}
