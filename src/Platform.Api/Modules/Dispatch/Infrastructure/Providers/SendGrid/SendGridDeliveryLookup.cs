using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;
using NotificationHub.Api.Modules.Dispatch.Integration.V1;
using NotificationHub.SharedKernel;
using Polly.Timeout;

namespace NotificationHub.Api.Modules.Dispatch.Infrastructure.Providers.SendGrid;

/// <summary>
/// Asks the provider what became of one e-mail whose callback never arrived,
/// over the message activity search. The search is by the custom arguments the
/// send attached, which are this hub's own correlation identifiers: no
/// destination is needed and none is sent, so the whole pull path for this
/// provider stays free of personal data.
/// <para>
/// How far back the search reaches is a commercial term of the contracted
/// plan, not a property of this code, so it is configuration
/// (<see cref="SendGridOptions.ActivityLookbackDays"/>) and the shipped value
/// is the reach of the plan without the paid activity add-on. A message older
/// than that is refused with its own code rather than asked about: the answer
/// would be an empty search, which reads exactly like a provider that denies
/// ever seeing the message.
/// </para>
/// <para>
/// The named client is this lookup's own, and not the one the send uses. A
/// read that times out is not a send that failed, and the send client carries
/// a circuit breaker whose whole meaning is how the provider is answering
/// sends.
/// </para>
/// </summary>
internal sealed class SendGridDeliveryLookup(
    IHttpClientFactory httpClientFactory,
    IOptions<SendGridOptions> options,
    TimeProvider timeProvider,
    ILogger<SendGridDeliveryLookup> logger) : IProviderDeliveryLookup
{
    internal const string HttpClientName = "dispatch-sendgrid-lookup";

    /// <summary>Custom argument carrying the notification identifier, as the send writes it.</summary>
    internal const string NotificationArgument = "notification_id";

    /// <summary>Custom argument carrying the attempt identifier, as the send writes it.</summary>
    internal const string AttemptArgument = "attempt_id";

    public string ProviderKey => SendGridChannelProvider.Key;

    public async Task<Result<IReadOnlyList<ProviderDeliveryEvent>>> LookupAsync(
        ProviderDeliveryQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        SendGridOptions config = options.Value;
        if (string.IsNullOrWhiteSpace(config.ApiKey))
        {
            logger.SendGridLookupNotConfigured(SendGridOptions.SectionName);
            return ProviderLookupRefusal.Refuse<IReadOnlyList<ProviderDeliveryEvent>>(
                ProviderLookupRefusal.LookupUnavailable);
        }

        var reach = TimeSpan.FromDays(config.ActivityLookbackDays);
        if (timeProvider.GetUtcNow() - query.SentAt > reach)
        {
            logger.SendGridLookupOutOfReach(config.ActivityLookbackDays);
            return ProviderLookupRefusal.Refuse<IReadOnlyList<ProviderDeliveryEvent>>(
                ProviderLookupRefusal.HistoryExhausted);
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, SearchPath(query, config));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.ApiKey);
            HttpClient client = httpClientFactory.CreateClient(HttpClientName);
            using HttpResponseMessage response = await client.SendAsync(request, cancellationToken);
            return await ReadAsync(response, query, cancellationToken);
        }
        catch (TimeoutRejectedException)
        {
            logger.SendGridLookupTimedOut(config.TimeoutSeconds);
            return ProviderLookupRefusal.Refuse<IReadOnlyList<ProviderDeliveryEvent>>(
                ProviderLookupRefusal.LookupUnavailable);
        }
        catch (HttpRequestException exception)
        {
            logger.SendGridLookupNetworkFault(exception);
            return ProviderLookupRefusal.Refuse<IReadOnlyList<ProviderDeliveryEvent>>(
                ProviderLookupRefusal.LookupUnavailable);
        }
    }

    /// <summary>
    /// The activity search for exactly one attempt, written in the provider's
    /// own query language over the custom arguments the send attached. Both
    /// identifiers are named, not just the notification: one notification can
    /// have several attempts on this channel, and a search that returned all
    /// of them would settle one attempt with another attempt's outcome.
    /// </summary>
    private static string SearchPath(ProviderDeliveryQuery query, SendGridOptions config)
    {
        var search =
            $"unique_args['{NotificationArgument}']=\"{query.Correlation.NotificationId}\" AND "
            + $"unique_args['{AttemptArgument}']=\"{query.Correlation.AttemptId}\"";
        return $"/v3/messages?limit={config.ActivityPageSize}&query={Uri.EscapeDataString(search)}";
    }

    private async Task<Result<IReadOnlyList<ProviderDeliveryEvent>>> ReadAsync(
        HttpResponseMessage response,
        ProviderDeliveryQuery query,
        CancellationToken cancellationToken)
    {
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
        {
            // The activity search is a paid capability on this provider, so a
            // refusal of the route itself is a contract fact and not an
            // outage. It is worth its own alarm, because every e-mail
            // reconciliation is silently doing nothing until it is fixed.
            logger.SendGridLookupUnavailable((int)response.StatusCode);
            return ProviderLookupRefusal.Refuse<IReadOnlyList<ProviderDeliveryEvent>>(
                ProviderLookupRefusal.LookupUnsupported);
        }

        if (!response.IsSuccessStatusCode)
        {
            logger.SendGridLookupRefused((int)response.StatusCode);
            return ProviderLookupRefusal.Refuse<IReadOnlyList<ProviderDeliveryEvent>>(
                ProviderLookupRefusal.LookupUnavailable);
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        SendGridActivityPage? page = Parse(body);
        if (page?.Messages is null)
        {
            logger.SendGridLookupPayloadUnreadable();
            return ProviderLookupRefusal.Refuse<IReadOnlyList<ProviderDeliveryEvent>>(
                ProviderLookupRefusal.PayloadUnreadable);
        }

        List<ProviderDeliveryEvent> events = [];
        foreach (SendGridActivityMessage message in page.Messages)
            if (Translate(message, query) is { } translated) events.Add(translated);

        if (events.Count == 0) logger.SendGridLookupFoundNothing();

        return Result.Success<IReadOnlyList<ProviderDeliveryEvent>>(events);
    }

    /// <summary>
    /// One activity entry as a canonical event.
    /// <para>
    /// The activity view speaks a coarser dialect than the callback: it says a
    /// message was not delivered and does not say why. That is translated as a
    /// failure with no suppression signal, deliberately. A signal is what
    /// closes a person's mailbox, the callback half classifies it from a
    /// vocabulary of provider reasons, and a status word carrying no reason at
    /// all must never be promoted into that decision.
    /// </para>
    /// </summary>
    private ProviderDeliveryEvent? Translate(SendGridActivityMessage message, ProviderDeliveryQuery query)
    {
        if (string.IsNullOrWhiteSpace(message.MessageId) || string.IsNullOrWhiteSpace(message.Status))
        {
            logger.SendGridLookupPayloadUnreadable();
            return null;
        }

        var status = message.Status.Trim().ToLowerInvariant();
        DeliveryFeedbackKind? kind = status switch
        {
            "processed" => DeliveryFeedbackKind.Sent,
            "delivered" => DeliveryFeedbackKind.Delivered,
            "not_delivered" => DeliveryFeedbackKind.Failed,
            _ => null,
        };
        if (kind is null)
        {
            logger.SendGridLookupStatusUnmapped(status);
            return null;
        }

        return new ProviderDeliveryEvent(
            ProviderKey,
            $"{message.MessageId}:{status}",
            kind.Value,
            Parsed(message.LastEventTime) ?? query.SentAt,
            message.MessageId,

            // The provider echoed the identifiers the send attached, which is
            // what the search asked by, so the correlation is proven rather
            // than assumed.
            query.Correlation,
            kind is DeliveryFeedbackKind.Failed ? status : null,
            SuppressionSignal.None);
    }

    private static DateTimeOffset? Parsed(string? value)
        => DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal,
            out DateTimeOffset parsed)
            ? parsed
            : null;

    private static SendGridActivityPage? Parse(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;

        try
        {
            return JsonSerializer.Deserialize<SendGridActivityPage>(body);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
