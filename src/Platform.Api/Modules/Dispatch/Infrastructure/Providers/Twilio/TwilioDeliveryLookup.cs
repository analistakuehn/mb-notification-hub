using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using NotificationHub.Api.Modules.Dispatch.Infrastructure.Webhooks;
using NotificationHub.Api.Modules.Dispatch.Integration.V1;
using NotificationHub.SharedKernel;
using Polly.Timeout;

namespace NotificationHub.Api.Modules.Dispatch.Infrastructure.Providers.Twilio;

/// <summary>
/// Asks the provider what became of one SMS whose callback never arrived, over
/// the Messages resource. Two routes, in this order: the message identity when
/// acceptance produced one, and otherwise the destination inside a time
/// window, which is all this provider offers because it does not search by the
/// metadata a caller attaches.
/// <para>
/// The destination route is best effort and says so in code: if the window
/// matches more than one message, the adapter concludes nothing. A message
/// picked out of an ambiguous set would settle an attempt with another
/// attempt's outcome, and on this channel that outcome can close a person's
/// number, so an unanswered attempt is the cheaper mistake.
/// </para>
/// <para>
/// The destination arrives per query and leaves with it. It is never stored on
/// this side, never logged and never echoed into an event: the canonical event
/// carries no destination by contract, and this adapter is the only place in
/// the pull path that ever holds one.
/// </para>
/// <para>
/// The named client is this lookup's own, and not the one the send uses. The
/// send client carries a circuit breaker whose whole meaning is how the
/// provider is answering sends; a read that times out is not a send that
/// failed, and feeding it into that breaker would stop a channel over a batch
/// job's bad minute.
/// </para>
/// </summary>
internal sealed class TwilioDeliveryLookup(
    IHttpClientFactory httpClientFactory,
    IOptions<TwilioOptions> options,
    IOptions<TwilioWebhookOptions> webhookOptions,
    ILogger<TwilioDeliveryLookup> logger) : IProviderDeliveryLookup
{
    internal const string HttpClientName = "dispatch-twilio-lookup";

    public string ProviderKey => TwilioChannelProvider.Key;

    public async Task<Result<IReadOnlyList<ProviderDeliveryEvent>>> LookupAsync(
        ProviderDeliveryQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        TwilioOptions config = options.Value;
        if (string.IsNullOrWhiteSpace(config.AccountSid)
            || string.IsNullOrWhiteSpace(config.CredentialSecret))
        {
            logger.TwilioLookupNotConfigured(TwilioOptions.SectionName);
            return ProviderLookupRefusal.Refuse<IReadOnlyList<ProviderDeliveryEvent>>(
                ProviderLookupRefusal.LookupUnavailable);
        }

        var byIdentity = query.ProviderMessageId is { Length: > 0 };
        if (!byIdentity && query.Target is not SmsDeliveryTarget)
        {
            // Neither route is open: this provider searches by nothing else,
            // and asking it anyway would spend a call to learn that.
            logger.TwilioLookupWithoutRoute();
            return ProviderLookupRefusal.Refuse<IReadOnlyList<ProviderDeliveryEvent>>(
                ProviderLookupRefusal.QueryUnusable);
        }

        var path = byIdentity
            ? MessagePath(config, query.ProviderMessageId!)
            : SearchPath(config, (SmsDeliveryTarget)query.Target!, query.SentAt);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Credential(config));
            HttpClient client = httpClientFactory.CreateClient(HttpClientName);
            using HttpResponseMessage response = await client.SendAsync(request, cancellationToken);
            return await ReadAsync(response, query, byIdentity, cancellationToken);
        }
        catch (TimeoutRejectedException)
        {
            logger.TwilioLookupTimedOut(config.TimeoutSeconds);
            return ProviderLookupRefusal.Refuse<IReadOnlyList<ProviderDeliveryEvent>>(
                ProviderLookupRefusal.LookupUnavailable);
        }
        catch (HttpRequestException exception)
        {
            logger.TwilioLookupNetworkFault(exception);
            return ProviderLookupRefusal.Refuse<IReadOnlyList<ProviderDeliveryEvent>>(
                ProviderLookupRefusal.LookupUnavailable);
        }
    }

    /// <summary>One message by its identity: the unambiguous route.</summary>
    private static string MessagePath(TwilioOptions config, string providerMessageId)
        => $"2010-04-01/Accounts/{Uri.EscapeDataString(config.AccountSid)}"
            + $"/Messages/{Uri.EscapeDataString(providerMessageId)}.json";

    /// <summary>
    /// Every message this account sent to one destination inside the window
    /// around the send. The window is configuration and deliberately narrow:
    /// it is the whole of what separates the message being asked about from
    /// the next message to the same person.
    /// </summary>
    private static string SearchPath(
        TwilioOptions config,
        SmsDeliveryTarget target,
        DateTimeOffset sentAt)
    {
        TimeSpan window = TimeSpan.FromSeconds(config.LookupWindowSeconds);
        var from = Instant(sentAt - window);
        var to = Instant(sentAt + window);
        return $"2010-04-01/Accounts/{Uri.EscapeDataString(config.AccountSid)}/Messages.json"
            + $"?To={Uri.EscapeDataString(target.PhoneNumber)}"
            + $"&DateSent%3E={from}&DateSent%3C={to}&PageSize={config.LookupPageSize}";
    }

    private static string Instant(DateTimeOffset moment)
        => Uri.EscapeDataString(
            moment.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));

    private static string Credential(TwilioOptions config)
    {
        var username = config.AuthenticationMode == TwilioAuthenticationMode.ApiKey
            ? config.ApiKeySid
            : config.AccountSid;
        return Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{config.CredentialSecret}"));
    }

    private async Task<Result<IReadOnlyList<ProviderDeliveryEvent>>> ReadAsync(
        HttpResponseMessage response,
        ProviderDeliveryQuery query,
        bool byIdentity,
        CancellationToken cancellationToken)
    {
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            // The provider holds no message under this identity. It is an
            // answer and not a fault: the attempt stays unsettled with a
            // record, and nothing is invented about it.
            logger.TwilioLookupFoundNothing();
            return Result.Success<IReadOnlyList<ProviderDeliveryEvent>>([]);
        }

        if (!response.IsSuccessStatusCode)
        {
            logger.TwilioLookupRefused((int)response.StatusCode);
            return ProviderLookupRefusal.Refuse<IReadOnlyList<ProviderDeliveryEvent>>(
                ProviderLookupRefusal.LookupUnavailable);
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        List<TwilioMessageResource>? messages = Parse(body, byIdentity);
        if (messages is null)
        {
            logger.TwilioLookupPayloadUnreadable();
            return ProviderLookupRefusal.Refuse<IReadOnlyList<ProviderDeliveryEvent>>(
                ProviderLookupRefusal.PayloadUnreadable);
        }

        if (messages.Count == 0)
        {
            logger.TwilioLookupFoundNothing();
            return Result.Success<IReadOnlyList<ProviderDeliveryEvent>>([]);
        }

        if (!byIdentity && messages.Count > 1)
        {
            // The correlation by destination and window is ambiguous here, and
            // an ambiguous correlation must not settle anything: whichever
            // message were picked, the attempt could end up carrying another
            // message's outcome.
            logger.TwilioLookupAmbiguous(messages.Count);
            return Result.Success<IReadOnlyList<ProviderDeliveryEvent>>([]);
        }

        TwilioWebhookOptions vocabulary = webhookOptions.Value;
        List<ProviderDeliveryEvent> events = [];
        foreach (TwilioMessageResource message in messages)
            if (Translate(message, query, vocabulary) is { } translated) events.Add(translated);

        return Result.Success<IReadOnlyList<ProviderDeliveryEvent>>(events);
    }

    /// <summary>
    /// One message resource as a canonical event. The correlation is the one
    /// the caller asked about: this provider echoes none of its own, and the
    /// caller named the attempt whose outcome it is missing, so answering
    /// about anything else would answer a question nobody asked.
    /// </summary>
    private ProviderDeliveryEvent? Translate(
        TwilioMessageResource message,
        ProviderDeliveryQuery query,
        TwilioWebhookOptions vocabulary)
    {
        if (string.IsNullOrWhiteSpace(message.Sid) || string.IsNullOrWhiteSpace(message.Status))
        {
            logger.TwilioLookupPayloadUnreadable();
            return null;
        }

        var status = TwilioDeliveryVocabulary.Normalize(message.Status);
        DeliveryFeedbackKind? kind = TwilioDeliveryVocabulary.Kind(status);
        if (kind is null)
        {
            // A word outside the mapped vocabulary leaves the attempt exactly
            // where it was. The callback half refuses the whole delivery for
            // the same reason this one drops the entry: neither guesses.
            logger.TwilioLookupStatusUnmapped(status);
            return null;
        }

        var errorCode = message.ErrorCode?.ToString(CultureInfo.InvariantCulture);
        SuppressionSignal signal = kind is DeliveryFeedbackKind.Bounced or DeliveryFeedbackKind.Failed
            ? SuppressionClassifier.Classify(
                errorCode,
                vocabulary.EffectiveInvalidDestinationCodes,
                vocabulary.EffectiveHardBounceCodes)
            : SuppressionSignal.None;

        return new ProviderDeliveryEvent(
            ProviderKey,
            TwilioDeliveryVocabulary.EventId(message.Sid, status),
            kind.Value,
            OccurredAt(message) ?? query.SentAt,
            message.Sid,
            query.Correlation,
            errorCode,
            signal);
    }

    private static DateTimeOffset? OccurredAt(TwilioMessageResource message)
        => Parsed(message.DateUpdated) ?? Parsed(message.DateSent);

    private static DateTimeOffset? Parsed(string? value)
        => DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal,
            out DateTimeOffset parsed)
            ? parsed
            : null;

    private static List<TwilioMessageResource>? Parse(string body, bool byIdentity)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;

        try
        {
            if (byIdentity)
            {
                TwilioMessageResource? single = JsonSerializer.Deserialize<TwilioMessageResource>(body);
                return single is null ? null : [single];
            }

            TwilioMessagePage? page = JsonSerializer.Deserialize<TwilioMessagePage>(body);
            return page?.Messages is null ? null : [.. page.Messages];
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
