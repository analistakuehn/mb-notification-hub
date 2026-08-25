using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
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
internal sealed class TwilioChannelProvider(
    IHttpClientFactory httpClientFactory,
    IOptions<TwilioOptions> options,
    ILogger<TwilioChannelProvider> logger) : IChannelProvider
{
    internal const string Key = "twilio";
    internal const string HttpClientName = "dispatch-twilio";

    /// <summary>Query parameter carrying the notification identifier back on the callback.</summary>
    internal const string NotificationIdParameter = "notificationId";

    /// <summary>Query parameter carrying the attempt identifier back on the callback.</summary>
    internal const string AttemptIdParameter = "attemptId";

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
        if (config.Product == TwilioSmsProduct.ProgrammableMessaging
            && string.IsNullOrWhiteSpace(config.MessagingServiceSid))
        {
            logger.TwilioSenderPoolAbsent();
        }

        using HttpRequestMessage httpRequest = BuildRequest(
            target, message, config, request.Correlation, request.Validity);
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
        TwilioOptions config,
        DispatchCorrelation? correlation = null,
        TimeSpan? validity = null)
    {
        var destination = target.PhoneNumber;
        return config.Product switch
        {
            TwilioSmsProduct.ProgrammableMessaging => new HttpRequestMessage(
                HttpMethod.Post,
                $"2010-04-01/Accounts/{Uri.EscapeDataString(config.AccountSid)}/Messages.json")
            {
                Content = new FormUrlEncodedContent(
                    BuildMessageForm(destination, message, config, correlation, validity)),
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

    /// <summary>
    /// The Programmable Messaging form. The sender is the Messaging Service
    /// when one is configured, so the provider picks from the sender pool and
    /// keeps the sticky sender per destination; without one the adapter falls
    /// back to the single verified number, which is what a local environment
    /// has. The callback address and the validity period join only when the
    /// caller supplied what they are made of.
    /// </summary>
    private static Dictionary<string, string> BuildMessageForm(
        string destination,
        SmsMessage message,
        TwilioOptions config,
        DispatchCorrelation? correlation,
        TimeSpan? validity)
    {
        var form = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["To"] = destination,
        };

        if (string.IsNullOrWhiteSpace(config.MessagingServiceSid))
        {
            form["From"] = config.FromNumber;
        }
        else
        {
            form["MessagingServiceSid"] = config.MessagingServiceSid;
        }

        form["Body"] = message.Body;
        if (StatusCallbackFor(config, correlation) is { } callback)
        {
            form["StatusCallback"] = callback;
        }

        if (ValidityPeriodFor(config, validity) is { } seconds)
        {
            form["ValidityPeriod"] = seconds.ToString(CultureInfo.InvariantCulture);
        }

        return form;
    }

    /// <summary>
    /// The address this hub asks the provider to report delivery to. The
    /// correlation identifiers ride in its query string because this provider
    /// echoes nothing back in the callback body; the parameter names are the
    /// members of <see cref="DispatchCorrelation"/> and the route that reads
    /// them binds by exactly those names. Without a configured address, or
    /// without correlation to carry, the send asks for no callback at all: a
    /// callback the hub cannot tie to an attempt is feedback nobody can apply.
    /// </summary>
    private static string? StatusCallbackFor(TwilioOptions config, DispatchCorrelation? correlation)
    {
        if (string.IsNullOrWhiteSpace(config.StatusCallbackUrl) || correlation is null) return null;

        var configured = config.StatusCallbackUrl;
        var separator = configured.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        return $"{configured}{separator}{NotificationIdParameter}="
            + $"{Uri.EscapeDataString(correlation.NotificationId.ToString())}"
            + $"&{AttemptIdParameter}={Uri.EscapeDataString(correlation.AttemptId.ToString())}";
    }

    /// <summary>
    /// The remaining validity translated into the provider's own knob, in
    /// whole seconds. A fraction of a second rounds up to one, because the
    /// caller already decided this send is worth making and a floor of zero
    /// would ask the provider for an impossible validity. Anything above the
    /// configured ceiling is sent as the ceiling.
    /// </summary>
    private static int? ValidityPeriodFor(TwilioOptions config, TimeSpan? validity)
    {
        if (validity is not { } remaining) return null;

        var seconds = (int)Math.Ceiling(remaining.TotalSeconds);
        return Math.Clamp(seconds, 1, config.MaxValidityPeriodSeconds);
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

    /// <summary>
    /// Fires at send time on purpose, like every configuration guard of this
    /// module: an environment without the SMS channel still boots. The
    /// destination guards are two distinct claims and stay separate: the
    /// pattern says the number is well formed, the prefix list says this
    /// deployment is allowed to address that market at all.
    /// </summary>
    private static void EnsureConfigured(TwilioOptions config, string destination, string body)
    {
        if (!config.DestinationExpression.IsMatch(destination))
        {
            throw new InvalidOperationException(
                $"The SMS destination does not match '{TwilioOptions.SectionName}:DestinationPattern'.");
        }

        IReadOnlyList<string> prefixes = config.EffectiveAllowedCountryPrefixes;
        if (prefixes.Count > 0
            && !prefixes.Any(prefix => destination.StartsWith(prefix, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                $"The SMS destination is outside '{TwilioOptions.SectionName}:AllowedCountryPrefixes'.");
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
            && string.IsNullOrWhiteSpace(config.MessagingServiceSid)
            && string.IsNullOrWhiteSpace(config.FromNumber))
        {
            throw new InvalidOperationException(
                $"Programmable Messaging requires '{TwilioOptions.SectionName}:MessagingServiceSid' "
                + $"or '{TwilioOptions.SectionName}:FromNumber'.");
        }

        if (config.Product == TwilioSmsProduct.Verify
            && (string.IsNullOrWhiteSpace(config.ServiceSid)
                || body.Length is < 4 or > 10))
        {
            throw new InvalidOperationException(
                $"'{TwilioOptions.SectionName}:ServiceSid' and an SMS body between 4 and 10 characters are required for Verify.");
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
}
