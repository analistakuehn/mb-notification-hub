using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using NotificationHub.Api.Modules.Dispatch.Integration.V1;
using NotificationHub.Api.Modules.TemplateManagement.Integration.V1;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace NotificationHub.Api.Modules.Dispatch.Infrastructure.Providers.Twilio;

/// <summary>
/// Twilio SMS adapter. Programmable Messaging sends the rendered body, while
/// Verify sends the body as the custom verification code configured by the
/// calling flow. The adapter never retries a send because the provider call is
/// not idempotent.
/// </summary>
internal sealed partial class TwilioChannelProvider(
    IHttpClientFactory httpClientFactory,
    IOptions<TwilioOptions> options,
    ILogger<TwilioChannelProvider> logger) : IChannelProvider
{
    internal const string Key = "twilio";
    internal const string HttpClientName = "dispatch-twilio";

    public Channel Channel => Channel.Sms;

    public string ProviderKey => Key;

    public async Task<ProviderResult> SendAsync(
        DispatchRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        (SmsDeliveryTarget target, SmsMessage message) = Discriminate(request);
        TwilioOptions config = options.Value;
        EnsureConfigured(config, target.PhoneNumber, message.Body);

        using HttpRequestMessage httpRequest = BuildRequest(target, message, config);
        var username = config.AuthenticationMode switch
        {
            TwilioAuthenticationMode.ApiKey => config.ApiKeySid,
            TwilioAuthenticationMode.AuthToken => config.AccountSid,
            _ => throw new InvalidOperationException(
                "Twilio authentication mode must be configured before sending."),
        };
        var credential = Convert.ToBase64String(
            Encoding.UTF8.GetBytes($"{username}:{config.CredentialSecret}"));
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Basic", credential);

        try
        {
            HttpClient client = httpClientFactory.CreateClient(HttpClientName);
            using HttpResponseMessage response = await client.SendAsync(httpRequest, cancellationToken);
            return await MapAsync(response, config, cancellationToken);
        }
        catch (BrokenCircuitException)
        {
            logger.TwilioCircuitOpen();
            return ProviderResult.Transient("circuit-open", null);
        }
        catch (TimeoutRejectedException)
        {
            logger.TwilioTimedOut(config.TimeoutSeconds);
            return ProviderResult.Transient("timeout", null);
        }
        catch (HttpRequestException exception)
        {
            logger.TwilioNetworkFault(exception);
            return ProviderResult.Transient("network", ProviderErrorSanitizer.Sanitize(exception.Message));
        }
    }

    internal static HttpRequestMessage BuildRequest(
        SmsDeliveryTarget target,
        SmsMessage message,
        TwilioOptions config)
    {
        var destination = target.PhoneNumber;
        return config.Product switch
        {
            TwilioSmsProduct.ProgrammableMessaging => new HttpRequestMessage(
                HttpMethod.Post,
                $"2010-04-01/Accounts/{Uri.EscapeDataString(config.AccountSid)}/Messages.json")
            {
                Content = new FormUrlEncodedContent(
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["To"] = destination,
                        ["From"] = config.FromNumber,
                        ["Body"] = message.Body,
                    }),
            },
            TwilioSmsProduct.Verify => new HttpRequestMessage(
                HttpMethod.Post,
                $"https://verify.twilio.com/v2/Services/{Uri.EscapeDataString(config.ServiceSid)}/Verifications")
            {
                Content = new FormUrlEncodedContent(
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["To"] = destination,
                        ["Channel"] = "sms",
                        ["CustomCode"] = message.Body,
                    }),
            },
            _ => throw new InvalidOperationException("Twilio product must be configured before sending."),
        };
    }

    private async Task<ProviderResult> MapAsync(
        HttpResponseMessage response,
        TwilioOptions config,
        CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var statusCode = (int)response.StatusCode;
        TwilioMessageResponse? payload = Deserialize(body);

        if (response.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK)
        {
            logger.TwilioAccepted(statusCode);
            return ProviderResult.Accepted(payload?.Sid);
        }

        var errorCode = payload?.Code?.ToString(CultureInfo.InvariantCulture)
            ?? $"http-{statusCode}";
        var errorMessage = ProviderErrorSanitizer.Sanitize(payload?.Message ?? body);
        logger.TwilioSendFailed(statusCode, errorCode);

        if (response.StatusCode is HttpStatusCode.TooManyRequests)
        {
            return ProviderResult.Throttled(errorCode, errorMessage, response.Headers.RetryAfter?.Delta);
        }

        return statusCode is >= 400 and < 500
            ? ProviderResult.Rejected(errorCode, errorMessage)
            : ProviderResult.Transient(errorCode, errorMessage);
    }

    private static (SmsDeliveryTarget Target, SmsMessage Message) Discriminate(DispatchRequest request)
    {
        if (request.Target is not SmsDeliveryTarget target || request.Message is not SmsMessage message)
        {
            throw new InvalidOperationException(
                "The Twilio adapter delivers SMS only; it received a request for another channel.");
        }

        return (target, message);
    }

    private static void EnsureConfigured(TwilioOptions config, string destination, string body)
    {
        if (!BrazilNumber().IsMatch(destination))
        {
            throw new InvalidOperationException(
                $"The SMS destination must be a Brazilian E.164 number under '{TwilioOptions.SectionName}:AllowedCountryPrefixes'.");
        }

        if (string.IsNullOrWhiteSpace(config.AccountSid)
            && config.AuthenticationMode == TwilioAuthenticationMode.AuthToken)
        {
            throw new InvalidOperationException($"Missing configuration '{TwilioOptions.SectionName}:AccountSid'.");
        }

        if (config.AuthenticationMode == TwilioAuthenticationMode.ApiKey
            && string.IsNullOrWhiteSpace(config.ApiKeySid))
        {
            throw new InvalidOperationException($"Missing configuration '{TwilioOptions.SectionName}:ApiKeySid'.");
        }

        if (string.IsNullOrWhiteSpace(config.CredentialSecret))
        {
            throw new InvalidOperationException($"Missing configuration '{TwilioOptions.SectionName}:CredentialSecret'.");
        }

        if (config.Product == TwilioSmsProduct.ProgrammableMessaging
            && string.IsNullOrWhiteSpace(config.FromNumber))
        {
            throw new InvalidOperationException($"Missing configuration '{TwilioOptions.SectionName}:FromNumber'.");
        }

        if (config.Product == TwilioSmsProduct.Verify
            && (string.IsNullOrWhiteSpace(config.ServiceSid)
                || body.Length is < 4 or > 10))
        {
            throw new InvalidOperationException(
                $"'{TwilioOptions.SectionName}:ServiceSid' and an SMS body between 4 and 10 characters are required for Verify.");
        }

        if (!config.AllowedCountryPrefixes.Contains("+55", StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                $"'{TwilioOptions.SectionName}:AllowedCountryPrefixes' must allow +55 for this local test.");
        }
    }

    private static TwilioMessageResponse? Deserialize(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<TwilioMessageResponse>(body);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    [GeneratedRegex(@"^\+55\d{10,11}$", RegexOptions.CultureInvariant)]
    private static partial Regex BrazilNumber();
}
