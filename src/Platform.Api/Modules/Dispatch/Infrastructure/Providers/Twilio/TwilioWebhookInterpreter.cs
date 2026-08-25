using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using NotificationHub.Api.Modules.Dispatch.Infrastructure.Webhooks;
using NotificationHub.Api.Modules.Dispatch.Integration.V1;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.Dispatch.Infrastructure.Providers.Twilio;

/// <summary>
/// Verifies and translates Twilio message status callbacks. The signature
/// scheme belongs to the provider: HMAC-SHA1 over the full request URL
/// followed by every form field, name then value, ordered by name, keyed by
/// the account auth token. The comparison runs in fixed time, because a
/// comparison that stops at the first differing byte tells an attacker how
/// much of a forged signature was right.
/// </summary>
internal sealed class TwilioWebhookInterpreter(
    IOptions<TwilioWebhookOptions> options,
    TimeProvider timeProvider,
    ILogger<TwilioWebhookInterpreter> logger) : IProviderWebhookInterpreter
{
    internal const string SignatureHeader = "X-Twilio-Signature";

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public string ProviderKey => TwilioChannelProvider.Key;

    public Result<VerifiedProviderWebhook> Verify(ProviderWebhookRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        TwilioWebhookOptions config = options.Value;

        if (!string.Equals(request.ProviderKey, ProviderKey, StringComparison.Ordinal))
        {
            return ProviderWebhookRefusal.Refuse<VerifiedProviderWebhook>(
                ProviderWebhookRefusal.ProviderUnknown);
        }

        // Origin is the first gate: it is the cheapest one, and it is the only
        // failure that means forgery rather than the everyday symptom of a
        // rotated secret, so a later refusal must not mask it.
        if (!WebhookRequestGuards.IsOriginAllowed(request.RemoteIpAddress, config.AllowedIpPrefixes))
        {
            logger.TwilioWebhookOriginRejected();
            return ProviderWebhookRefusal.Refuse<VerifiedProviderWebhook>(
                ProviderWebhookRefusal.OriginNotAllowed);
        }

        if (string.IsNullOrWhiteSpace(config.AuthToken))
        {
            logger.TwilioWebhookSecretMissing(TwilioWebhookOptions.SectionName);
            return ProviderWebhookRefusal.Refuse<VerifiedProviderWebhook>(
                ProviderWebhookRefusal.SignatureInvalid);
        }

        if (!TryReadForm(request.Body, out List<KeyValuePair<string, string>> parameters))
        {
            logger.TwilioWebhookPayloadUnreadable();
            return ProviderWebhookRefusal.Refuse<VerifiedProviderWebhook>(
                ProviderWebhookRefusal.PayloadUnreadable);
        }

        DateTimeOffset now = timeProvider.GetUtcNow();
        var declaredTimestamp = Find(parameters, config.TimestampParameterName);
        if (declaredTimestamp is not null
            && !WebhookRequestGuards.IsWithinWindow(
                declaredTimestamp, now, config.TimestampWindowSeconds, out _))
        {
            logger.TwilioWebhookTimestampOutOfWindow(config.TimestampWindowSeconds);
            return ProviderWebhookRefusal.Refuse<VerifiedProviderWebhook>(
                ProviderWebhookRefusal.TimestampOutOfWindow);
        }

        var presented = WebhookRequestGuards.FindHeader(request.Headers, SignatureHeader);
        var provided = WebhookRequestGuards.TryDecodeBase64(presented);
        var expected = ComputeSignature(request.RequestUrl, parameters, config.AuthToken);
        if (provided is null || !CryptographicOperations.FixedTimeEquals(provided, expected))
        {
            logger.TwilioWebhookSignatureRejected();
            return ProviderWebhookRefusal.Refuse<VerifiedProviderWebhook>(
                ProviderWebhookRefusal.SignatureInvalid);
        }

        logger.TwilioWebhookVerified();
        return Result.Success(new VerifiedProviderWebhook(ProviderKey, now, request.Body));
    }

    public Result<IReadOnlyList<ProviderDeliveryEvent>> Interpret(VerifiedProviderWebhook webhook)
    {
        ArgumentNullException.ThrowIfNull(webhook);

        if (!string.Equals(webhook.ProviderKey, ProviderKey, StringComparison.Ordinal))
        {
            return ProviderWebhookRefusal.Refuse<IReadOnlyList<ProviderDeliveryEvent>>(
                ProviderWebhookRefusal.ProviderUnknown);
        }

        if (!TryReadForm(webhook.Body, out List<KeyValuePair<string, string>> parameters))
        {
            logger.TwilioWebhookPayloadUnreadable();
            return ProviderWebhookRefusal.Refuse<IReadOnlyList<ProviderDeliveryEvent>>(
                ProviderWebhookRefusal.PayloadUnreadable);
        }

        var messageSid = Find(parameters, "MessageSid");
        var declaredStatus = Find(parameters, "MessageStatus");
        if (string.IsNullOrWhiteSpace(messageSid) || string.IsNullOrWhiteSpace(declaredStatus))
        {
            logger.TwilioWebhookPayloadUnreadable();
            return ProviderWebhookRefusal.Refuse<IReadOnlyList<ProviderDeliveryEvent>>(
                ProviderWebhookRefusal.PayloadUnreadable);
        }

        var status = TwilioDeliveryVocabulary.Normalize(declaredStatus);
        DeliveryFeedbackKind? kind = TwilioDeliveryVocabulary.Kind(status);
        if (kind is null)
        {
            // A word outside the mapped vocabulary is a provider change, not
            // noise: guessing a canonical meaning would move an attempt into
            // the wrong state, so the callback is refused loudly instead.
            logger.TwilioWebhookStatusUnmapped(status);
            return ProviderWebhookRefusal.Refuse<IReadOnlyList<ProviderDeliveryEvent>>(
                ProviderWebhookRefusal.PayloadUnreadable);
        }

        var declaredErrorCode = Find(parameters, "ErrorCode");
        var errorCode = string.IsNullOrWhiteSpace(declaredErrorCode) ? null : declaredErrorCode.Trim();

        TwilioWebhookOptions config = options.Value;
        SuppressionSignal signal = kind is DeliveryFeedbackKind.Bounced or DeliveryFeedbackKind.Failed
            ? SuppressionClassifier.Classify(
                errorCode,
                config.EffectiveInvalidDestinationCodes,
                config.EffectiveHardBounceCodes)
            : SuppressionSignal.None;

        // The identity is minted by the shared vocabulary, so the reading
        // this module pulls later lands on the very same one.
        var providerEventId = TwilioDeliveryVocabulary.EventId(messageSid, status);

        // The correlation identifiers ride in the callback URL, which a
        // verified webhook deliberately does not carry, so this provider
        // correlates through the message identifier at the consumer.
        ProviderDeliveryEvent deliveryEvent = new(
            ProviderKey,
            providerEventId,
            kind.Value,
            webhook.VerifiedAt,
            messageSid,
            null,
            errorCode,
            signal);

        return Result.Success<IReadOnlyList<ProviderDeliveryEvent>>([deliveryEvent]);
    }

    internal static byte[] ComputeSignature(
        string requestUrl,
        IReadOnlyList<KeyValuePair<string, string>> orderedParameters,
        string authToken)
    {
        var payload = new StringBuilder(requestUrl);
        foreach (KeyValuePair<string, string> parameter in orderedParameters) payload.Append(parameter.Key).Append(parameter.Value);

        // The algorithm is dictated by the provider, which signs its callbacks
        // with HMAC-SHA1 and offers no stronger variant. Refusing it here would
        // not make the callbacks stronger, it would make them unverifiable; the
        // collision weakness of the hash does not break the keyed construction
        // used for this authentication, and the key never leaves the secret store.
#pragma warning disable CA5350 // Provider-dictated signature algorithm.
        return HMACSHA1.HashData(
            Encoding.UTF8.GetBytes(authToken),
            Encoding.UTF8.GetBytes(payload.ToString()));
#pragma warning restore CA5350
    }

    private static string? Find(List<KeyValuePair<string, string>> parameters, string name)
    {
        foreach (KeyValuePair<string, string> parameter in parameters)
            if (string.Equals(parameter.Key, name, StringComparison.Ordinal)) return parameter.Value;

        return null;
    }

    // Parses the form body and orders it the way the signature recipe of the
    // provider demands. Repeated names are ordered by value too, so a body
    // that repeats a field still hashes to one deterministic payload.
    private static bool TryReadForm(
        ReadOnlyMemory<byte> body,
        out List<KeyValuePair<string, string>> parameters)
    {
        parameters = [];

        string text;
        try
        {
            text = StrictUtf8.GetString(body.Span);
        }
        catch (DecoderFallbackException)
        {
            return false;
        }

        foreach (var pair in text.Split('&'))
        {
            if (pair.Length == 0) continue;

            var separator = pair.IndexOf('=');
            var name = separator < 0 ? pair : pair[..separator];
            var value = separator < 0 ? "" : pair[(separator + 1)..];
            parameters.Add(new KeyValuePair<string, string>(Decode(name), Decode(value)));
        }

        parameters.Sort(static (left, right) =>
        {
            var byName = string.CompareOrdinal(left.Key, right.Key);
            return byName != 0 ? byName : string.CompareOrdinal(left.Value, right.Value);
        });

        return true;
    }

    private static string Decode(string value)
        => Uri.UnescapeDataString(value.Replace('+', ' '));
}
