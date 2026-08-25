using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using NotificationHub.Api.Modules.Dispatch.Infrastructure.Webhooks;
using NotificationHub.Api.Modules.Dispatch.Integration.V1;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.Dispatch.Infrastructure.Providers.SendGrid;

/// <summary>
/// Verifies and translates SendGrid event callbacks. The signature scheme
/// belongs to the provider: an elliptic-curve signature over the request
/// timestamp followed by the raw body, checked against the public key the
/// provider publishes. Because the timestamp is inside the signed payload,
/// the replay window is not optional here: a captured callback stays
/// cryptographically valid forever, and replaying it would walk an attempt
/// backwards through its state machine.
/// </summary>
internal sealed class SendGridWebhookInterpreter(
    IOptions<SendGridWebhookOptions> options,
    TimeProvider timeProvider,
    ILogger<SendGridWebhookInterpreter> logger) : IProviderWebhookInterpreter
{
    internal const string SignatureHeader = "X-Twilio-Email-Event-Webhook-Signature";
    internal const string TimestampHeader = "X-Twilio-Email-Event-Webhook-Timestamp";

    public string ProviderKey => SendGridChannelProvider.Key;

    public Result<VerifiedProviderWebhook> Verify(ProviderWebhookRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        SendGridWebhookOptions config = options.Value;

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
            logger.SendGridWebhookOriginRejected();
            return ProviderWebhookRefusal.Refuse<VerifiedProviderWebhook>(
                ProviderWebhookRefusal.OriginNotAllowed);
        }

        var publicKey = WebhookRequestGuards.TryDecodeBase64(config.PublicKey);
        if (publicKey is null)
        {
            logger.SendGridWebhookKeyUnusable(SendGridWebhookOptions.SectionName);
            return ProviderWebhookRefusal.Refuse<VerifiedProviderWebhook>(
                ProviderWebhookRefusal.SignatureInvalid);
        }

        DateTimeOffset now = timeProvider.GetUtcNow();
        var declaredTimestamp = WebhookRequestGuards.FindHeader(request.Headers, TimestampHeader);
        if (!WebhookRequestGuards.IsWithinWindow(
                declaredTimestamp, now, config.TimestampWindowSeconds, out _))
        {
            logger.SendGridWebhookTimestampOutOfWindow(config.TimestampWindowSeconds);
            return ProviderWebhookRefusal.Refuse<VerifiedProviderWebhook>(
                ProviderWebhookRefusal.TimestampOutOfWindow);
        }

        var presented = WebhookRequestGuards.FindHeader(request.Headers, SignatureHeader);
        var signature = WebhookRequestGuards.TryDecodeBase64(presented);
        if (signature is null
            || !IsSignatureValid(publicKey, signature, declaredTimestamp!, request.Body))
        {
            logger.SendGridWebhookSignatureRejected();
            return ProviderWebhookRefusal.Refuse<VerifiedProviderWebhook>(
                ProviderWebhookRefusal.SignatureInvalid);
        }

        logger.SendGridWebhookVerified();
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

        SendGridWebhookOptions config = options.Value;
        List<ProviderDeliveryEvent> events = [];

        try
        {
            using var document = JsonDocument.Parse(webhook.Body);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                logger.SendGridWebhookPayloadUnreadable();
                return ProviderWebhookRefusal.Refuse<IReadOnlyList<ProviderDeliveryEvent>>(
                    ProviderWebhookRefusal.PayloadUnreadable);
            }

            foreach (JsonElement element in document.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object)
                {
                    logger.SendGridWebhookPayloadUnreadable();
                    return ProviderWebhookRefusal.Refuse<IReadOnlyList<ProviderDeliveryEvent>>(
                        ProviderWebhookRefusal.PayloadUnreadable);
                }

                Result<ProviderDeliveryEvent?> translated = Translate(element, webhook.VerifiedAt, config);
                if (translated.IsFailure)
                {
                    return ProviderWebhookRefusal.Refuse<IReadOnlyList<ProviderDeliveryEvent>>(
                        translated.Error!);
                }

                if (translated.Value is not null) events.Add(translated.Value);
            }
        }
        catch (JsonException)
        {
            logger.SendGridWebhookPayloadUnreadable();
            return ProviderWebhookRefusal.Refuse<IReadOnlyList<ProviderDeliveryEvent>>(
                ProviderWebhookRefusal.PayloadUnreadable);
        }

        return Result.Success<IReadOnlyList<ProviderDeliveryEvent>>(events);
    }

    private static bool IsSignatureValid(
        byte[] publicKey,
        byte[] signature,
        string timestamp,
        ReadOnlyMemory<byte> body)
    {
        var prefix = Encoding.UTF8.GetBytes(timestamp);
        var signed = new byte[prefix.Length + body.Length];
        prefix.CopyTo(signed, 0);
        body.Span.CopyTo(signed.AsSpan(prefix.Length));

        try
        {
            using var verifier = ECDsa.Create();
            verifier.ImportSubjectPublicKeyInfo(publicKey, out _);
            return verifier.VerifyData(
                signed,
                signature,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (CryptographicException)
        {
            // An unusable key and a malformed signature are the same answer
            // to the caller: these bytes were not proven to come from the
            // provider. The distinction is an operator concern and travels in
            // the log, not in the refusal code.
            return false;
        }
    }

    // Returns a null value on success when the batch entry is an event this
    // hub does not track: a callback mixes delivery events with engagement
    // events, and refusing the batch over a click would drop real feedback.
    private Result<ProviderDeliveryEvent?> Translate(
        JsonElement element,
        DateTimeOffset verifiedAt,
        SendGridWebhookOptions config)
    {
        var providerEventId = ReadString(element, "sg_event_id");
        var declaredEvent = ReadString(element, "event");
        if (string.IsNullOrWhiteSpace(providerEventId) || string.IsNullOrWhiteSpace(declaredEvent))
        {
            logger.SendGridWebhookPayloadUnreadable();
            return ProviderWebhookRefusal.Refuse<ProviderDeliveryEvent?>(
                ProviderWebhookRefusal.PayloadUnreadable);
        }

        var eventName = declaredEvent.Trim().ToLowerInvariant();
        var bounceType = ReadString(element, "type");
        var reason = ReadString(element, "reason");

        SuppressionSignal signal = eventName switch
        {
            "bounce" => SuppressionClassifier.Classify(
                bounceType ?? "bounce",
                config.EffectiveInvalidDestinationCodes,
                config.EffectiveHardBounceCodes),
            "dropped" => SuppressionClassifier.Classify(
                reason,
                config.EffectiveInvalidDestinationCodes,
                config.EffectiveHardBounceCodes),
            _ => SuppressionSignal.None,
        };

        DeliveryFeedbackKind? kind = eventName switch
        {
            "processed" or "deferred" => DeliveryFeedbackKind.Sent,
            "delivered" => DeliveryFeedbackKind.Delivered,
            "open" => DeliveryFeedbackKind.Read,
            "bounce" => DeliveryFeedbackKind.Bounced,
            // A drop is the destination talking only when the reason accuses
            // the destination; otherwise the provider stopped the message on
            // its own and the attempt merely failed.
            "dropped" => signal is SuppressionSignal.None
                ? DeliveryFeedbackKind.Failed
                : DeliveryFeedbackKind.Bounced,
            "blocked" => DeliveryFeedbackKind.Failed,
            _ => null,
        };

        if (kind is null)
        {
            if (!WebhookRequestGuards.Names(config.EffectiveUntrackedEvents, eventName)) logger.SendGridWebhookEventUnmapped(eventName);

            return Result.Success<ProviderDeliveryEvent?>(null);
        }

        return Result.Success<ProviderDeliveryEvent?>(new ProviderDeliveryEvent(
            ProviderKey,
            providerEventId,
            kind.Value,
            ReadTimestamp(element) ?? verifiedAt,
            ReadString(element, "sg_message_id"),
            ReadCorrelation(element),
            ProviderErrorSanitizer.Sanitize(ReadString(element, "status") ?? bounceType ?? reason),
            signal));
    }

    private static DispatchCorrelation? ReadCorrelation(JsonElement element)
        => Guid.TryParse(ReadCustomArgument(element, "notification_id"), out Guid notificationId)
            && Guid.TryParse(ReadCustomArgument(element, "attempt_id"), out Guid attemptId)
                ? new DispatchCorrelation(notificationId, attemptId)
                : null;

    // The provider flattens custom arguments into the event object, and its
    // documentation presents them nested; both shapes are read so a change of
    // shape does not silently drop the correlation.
    private static string? ReadCustomArgument(JsonElement element, string name)
    {
        var direct = ReadString(element, name);
        if (direct is not null) return direct;

        return element.TryGetProperty("custom_args", out JsonElement arguments)
               && arguments.ValueKind == JsonValueKind.Object
                ? ReadString(arguments, name)
                : null;
    }

    private static DateTimeOffset? ReadTimestamp(JsonElement element)
        => element.TryGetProperty("timestamp", out JsonElement value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out var seconds)
            && seconds is >= 0 and <= 253_402_300_799
                ? DateTimeOffset.FromUnixTimeSeconds(seconds)
                : null;

    private static string? ReadString(JsonElement element, string name)
        => element.TryGetProperty(name, out JsonElement value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
}
