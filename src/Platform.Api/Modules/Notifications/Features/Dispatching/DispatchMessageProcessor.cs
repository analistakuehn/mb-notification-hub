using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NotificationHub.Api.Infrastructure.Cryptography;
using NotificationHub.Api.Infrastructure.Messaging.Consuming;
using NotificationHub.Api.Modules.ContactConsent.Integration.V1;
using NotificationHub.Api.Modules.Dispatch.Integration.V1;
using NotificationHub.Api.Modules.Notifications.Domain;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Persistence;
using NotificationHub.Api.Modules.TemplateManagement.Integration.V1;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.Notifications.Features.Dispatching;

/// <summary>
/// How one provider verdict settles the claimed attempt. Derived from the
/// normalized outcome alone, never from provider details.
/// </summary>
internal enum DispatchVerdict
{
    /// <summary>The provider took the message: the attempt reaches sent.</summary>
    Sent = 0,

    /// <summary>Permanent rejection: the attempt fails and the plan may advance.</summary>
    Failed = 1,

    /// <summary>
    /// The provider provably did not take the call (throttled or open
    /// circuit): the attempt returns to queued and the message comes back.
    /// </summary>
    Requeue = 2,

    /// <summary>No conclusive verdict: the attempt parks on unknown.</summary>
    Unknown = 3,
}

/// <summary>
/// Consumer-side entry of the dispatch queues: reads the claim check, takes
/// ownership of the attempt through the optimistic lock, reveals the
/// destination transiently, performs exactly one provider call and settles
/// the verdict transactionally. Contact value and device token exist in
/// memory only, between the reveal and the send. A redelivery resolves on
/// the stored status: an attempt no longer queued is never sent again,
/// because a provider send is not idempotent.
/// </summary>
internal sealed class DispatchMessageProcessor(
    NotificationsDbContext db,
    AttemptDispatchWriter writer,
    IChannelProviderResolver providerResolver,
    IRecipientDirectory recipientDirectory,
    IDeviceTokenLifecycle deviceTokenLifecycle,
    IEnvelopeCipher cipher,
    ILogger<DispatchMessageProcessor> logger) : ISqsMessageProcessor
{
    internal const int SupportedSchemaVersion = DispatchMessages.SchemaVersion;
    internal const string ReasonPayloadWithoutIds = "payload-missing-attempt-reference";
    internal const string ReasonAttemptNotFound = "attempt-not-found";
    internal const string ReasonNotificationNotFound = "notification-not-found";
    internal const string ReasonProviderThrottled = "provider-throttled";
    internal const string ReasonCircuitOpen = "circuit-open";

    /// <summary>Stable code of a send whose contact point vanished between routing and send.</summary>
    internal const string ErrorContactPointUnavailable = "contact-point-unavailable";

    /// <summary>Stable code of a send whose device token was invalidated between claim and send.</summary>
    internal const string ErrorDeviceTokenInactive = "device-token-inactive";

    /// <summary>Adapter code of an open circuit: the only transient error that proves no call was taken.</summary>
    internal const string CircuitOpenErrorCode = "circuit-open";

    /// <summary>Provider codes that invalidate the device token after the verdict commits.</summary>
    private static readonly string[] TokenInvalidationCodes = ["UNREGISTERED", "INVALID_ARGUMENT"];

    private const string AuthQueueSuffix = "-auth";

    public string Consumer => AttemptDispatchWriter.ConsumerName;

    public bool Accepts(string type, int schemaVersion)
        => string.Equals(type, DispatchMessages.AttemptQueuedType, StringComparison.Ordinal)
            && schemaVersion == SupportedSchemaVersion;

    public async Task<MessageDisposition> ProcessAsync(
        MessageEnvelope envelope,
        CancellationToken cancellationToken)
    {
        if (!TryReadGuid(envelope.Payload, "notificationId", out Guid notificationId)
            || !TryReadGuid(envelope.Payload, "attemptId", out Guid attemptId))
        {
            return new MessageDisposition.Discard(ReasonPayloadWithoutIds);
        }

        NotificationAttempt? attempt = await db.NotificationAttempts
            .FirstOrDefaultAsync(candidate => candidate.Id == attemptId, cancellationToken);
        if (attempt is null)
        {
            // The outbox commits with the attempt, so an absent row means the
            // claim check outlived its state: permanently unprocessable.
            return new MessageDisposition.Discard(ReasonAttemptNotFound);
        }

        Notification? notification = await db.Notifications
            .FirstOrDefaultAsync(candidate => candidate.Id == notificationId, cancellationToken);
        if (notification is null)
        {
            return new MessageDisposition.Discard(ReasonNotificationNotFound);
        }

        if (attempt.Status != NotificationAttemptStatuses.Queued)
        {
            // Redelivery after a claim or a verdict: the stored status is the
            // authority and the send never repeats. An attempt parked on
            // sending by a crash stays for reconciliation, never a resend.
            logger.DispatchDuplicateSkipped(attempt.Id, attempt.Status);
            return new MessageDisposition.Duplicate();
        }

        Result<Channel> channel = Channel.Create(attempt.Channel);
        if (channel.IsFailure)
        {
            return new MessageDisposition.Discard($"channel-unknown:{attempt.Channel}");
        }

        Result<IChannelProvider> provider = await providerResolver.ResolveAsync(
            channel.Value!, cancellationToken);
        if (provider.IsFailure)
        {
            // Configuration or deployment defect: transient by contract, the
            // message returns with backoff and heals without loss.
            throw new InvalidOperationException(provider.Error);
        }

        var isPush = string.Equals(
            attempt.Channel, AttemptDispatchWriter.PushChannel, StringComparison.Ordinal);
        Guid? deviceTokenId = attempt.DeviceTokenId;
        if (isPush && deviceTokenId is null)
        {
            IReadOnlyList<Guid> tokens = await ResolveActiveTokensAsync(
                notification, envelope, cancellationToken);
            if (tokens.Count == 0)
            {
                var settled = await writer.TryFailWithoutTokensAsync(
                    attempt, notification, envelope.MessageId, cancellationToken);
                if (settled)
                {
                    logger.DispatchAttemptFailed(
                        attempt.Id, notification.Id, AttemptDispatchWriter.ErrorNoActiveDeviceToken);
                }

                return settled ? new MessageDisposition.Processed() : new MessageDisposition.Duplicate();
            }

            var sourceQueue = envelope.SourceQueue
                ?? throw new InvalidOperationException(
                    "A expansão do fan-out requer a fila de origem carimbada no envelope.");
            AttemptClaimOutcome expanded = await writer.TryClaimPushAsync(
                attempt, notification, provider.Value!.ProviderKey, tokens, sourceQueue, cancellationToken);
            if (expanded == AttemptClaimOutcome.NotQueued)
            {
                logger.DispatchDuplicateSkipped(attempt.Id, attempt.Status);
                return new MessageDisposition.Duplicate();
            }

            deviceTokenId = tokens[0];
            logger.DispatchFanOutExpanded(notification.Id, attempt.Id, tokens.Count);
        }
        else
        {
            AttemptClaimOutcome claimed = await writer.TryClaimAsync(
                attempt, provider.Value!.ProviderKey, cancellationToken);
            if (claimed == AttemptClaimOutcome.NotQueued)
            {
                logger.DispatchDuplicateSkipped(attempt.Id, attempt.Status);
                return new MessageDisposition.Duplicate();
            }
        }

        Result<DeliveryTarget> target = await ResolveTargetAsync(
            notification, attempt, isPush, deviceTokenId, cancellationToken);
        if (target.IsFailure)
        {
            var errorCode = isPush ? ErrorDeviceTokenInactive : ErrorContactPointUnavailable;
            var settled = await writer.RecordFailureAsync(
                attempt, notification, errorCode, envelope.MessageId, cancellationToken);
            if (settled)
            {
                logger.DispatchAttemptFailed(attempt.Id, notification.Id, errorCode);
            }

            return settled ? new MessageDisposition.Processed() : new MessageDisposition.Duplicate();
        }

        StoredAttemptContent content = await StoredAttemptContent.OpenAsync(
            cipher, notification.Application, attempt.RenderedContentEncrypted, cancellationToken);
        var request = new DispatchRequest(
            target.Value!,
            content.ToRenderedMessage(),
            new DispatchCorrelation(notification.Id, attempt.Id));

        ProviderResult result = await provider.Value!.SendAsync(request, cancellationToken);
        return await SettleVerdictAsync(
            envelope, notification, attempt, provider.Value!.ProviderKey, isPush, deviceTokenId,
            result, cancellationToken);
    }

    /// <summary>Maps the normalized provider outcome to the attempt transition it commands.</summary>
    internal static DispatchVerdict Decide(ProviderResult result)
        => result.Outcome switch
        {
            ProviderOutcome.Accepted => DispatchVerdict.Sent,
            ProviderOutcome.Rejected => DispatchVerdict.Failed,
            ProviderOutcome.Throttled => DispatchVerdict.Requeue,
            ProviderOutcome.TransientError when string.Equals(
                result.ErrorCode, CircuitOpenErrorCode, StringComparison.Ordinal) => DispatchVerdict.Requeue,
            ProviderOutcome.TransientError => DispatchVerdict.Unknown,
            _ => throw new InvalidOperationException($"Desfecho de provedor não suportado: {result.Outcome}."),
        };

    private async Task<MessageDisposition> SettleVerdictAsync(
        MessageEnvelope envelope,
        Notification notification,
        NotificationAttempt attempt,
        string providerKey,
        bool isPush,
        Guid? deviceTokenId,
        ProviderResult result,
        CancellationToken cancellationToken)
    {
        switch (Decide(result))
        {
            case DispatchVerdict.Sent:
                var sent = await writer.RecordSentAsync(
                    attempt, notification, providerKey, result.ProviderMessageId,
                    envelope.MessageId, deliveredOnAcceptance: isPush, cancellationToken);
                if (sent)
                {
                    logger.DispatchAttemptSent(attempt.Id, notification.Id, providerKey);
                }

                return sent ? new MessageDisposition.Processed() : new MessageDisposition.Duplicate();
            case DispatchVerdict.Failed:
                var errorCode = result.ErrorCode ?? "provider-rejected";
                var failed = await writer.RecordFailureAsync(
                    attempt, notification, errorCode, envelope.MessageId, cancellationToken);
                if (failed)
                {
                    logger.DispatchAttemptFailed(attempt.Id, notification.Id, errorCode);
                    if (isPush && deviceTokenId is { } tokenId
                        && TokenInvalidationCodes.Contains(result.ErrorCode, StringComparer.Ordinal))
                    {
                        // After the verdict commit and outside its transaction
                        // on purpose: the invalidation is the owning module's
                        // own transactional write, idempotent on repetition.
                        await ReportDeadTokenAsync(
                            notification.RecipientId, tokenId, result.ErrorCode!, cancellationToken);
                    }
                }

                return failed ? new MessageDisposition.Processed() : new MessageDisposition.Duplicate();
            case DispatchVerdict.Requeue:
                await writer.RevertToQueuedAsync(attempt, cancellationToken);
                var reason = result.Outcome == ProviderOutcome.Throttled
                    ? ReasonProviderThrottled
                    : ReasonCircuitOpen;
                logger.DispatchAttemptRequeued(attempt.Id, notification.Id, reason);
                return new MessageDisposition.Postponed(result.RetryAfter, reason);
            case DispatchVerdict.Unknown:
                var parked = await writer.RecordUnknownAsync(
                    attempt, result.ErrorCode, envelope.MessageId, cancellationToken);
                if (parked)
                {
                    logger.DispatchAttemptUnknown(attempt.Id, notification.Id, result.ErrorCode);
                }

                return parked ? new MessageDisposition.Processed() : new MessageDisposition.Duplicate();
            default:
                throw new InvalidOperationException("Veredito de despacho não suportado.");
        }
    }

    /// <summary>
    /// Reads the active device tokens for the fan-out: ids and recency only,
    /// most recent first, capped at the fan-out limit. Critical and
    /// authentication traffic tolerates the last known snapshot, mirroring
    /// the pipeline's degradation rule; an unknown recipient means zero
    /// tokens.
    /// </summary>
    private async Task<IReadOnlyList<Guid>> ResolveActiveTokensAsync(
        Notification notification,
        MessageEnvelope envelope,
        CancellationToken cancellationToken)
    {
        RecipientReadFallback fallback =
            notification.Class == NotificationClasses.Critical
                || envelope.SourceQueue?.EndsWith(AuthQueueSuffix, StringComparison.Ordinal) is true
                    ? RecipientReadFallback.LastKnown
                    : RecipientReadFallback.None;
        Result<RecipientSnapshot> snapshot = await recipientDirectory.FindAsync(
            notification.RecipientId, fallback, cancellationToken);
        if (snapshot.IsFailure)
        {
            return [];
        }

        return [.. snapshot.Value!.Devices
            .OrderByDescending(device => device.LastSeenAt)
            .Take(AttemptDispatchWriter.PushFanOutLimit)
            .Select(device => device.DeviceTokenId)];
    }

    /// <summary>
    /// Reveals the destination transiently: the plaintext exists between here
    /// and the provider call, in memory only.
    /// </summary>
    private async Task<Result<DeliveryTarget>> ResolveTargetAsync(
        Notification notification,
        NotificationAttempt attempt,
        bool isPush,
        Guid? deviceTokenId,
        CancellationToken cancellationToken)
    {
        if (isPush)
        {
            if (deviceTokenId is not { } tokenId)
            {
                return Result.NotFound<DeliveryTarget>("O attempt de push não carrega um token de dispositivo.");
            }

            Result<string> token = await recipientDirectory.RevealDeviceTokenAsync(
                notification.RecipientId, tokenId, cancellationToken);
            return token.IsFailure
                ? new Result<DeliveryTarget>(false, default, token.ErrorKind, token.Error)
                : Result.Success<DeliveryTarget>(new PushDeliveryTarget(token.Value!));
        }

        if (attempt.ContactPointId is not { } contactPointId)
        {
            return Result.NotFound<DeliveryTarget>("O attempt não referencia um ponto de contato.");
        }

        Result<string> value = await recipientDirectory.RevealContactValueAsync(
            notification.RecipientId, contactPointId, cancellationToken);
        return value.IsFailure
            ? new Result<DeliveryTarget>(false, default, value.ErrorKind, value.Error)
            : Result.Success<DeliveryTarget>(new EmailDeliveryTarget(value.Value!));
    }

    /// <summary>
    /// Best effort by design: the verdict already committed and a redelivery
    /// resolves as a duplicate, so a failed report here cannot be retried by
    /// the queue. The failure is logged and the reconciliation of a later
    /// phase remains the safety net.
    /// </summary>
    private async Task ReportDeadTokenAsync(
        string recipientId,
        Guid deviceTokenId,
        string providerCode,
        CancellationToken cancellationToken)
    {
        try
        {
            Result invalidated = await deviceTokenLifecycle.InvalidateDeviceTokenAsync(
                recipientId, deviceTokenId, providerCode, cancellationToken);
            if (invalidated.IsFailure)
            {
                logger.DispatchTokenInvalidationFailed(deviceTokenId, invalidated.Error ?? providerCode);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.DispatchTokenInvalidationThrew(deviceTokenId, exception);
        }
    }

    private static bool TryReadGuid(JsonElement payload, string name, out Guid value)
    {
        value = default;
        return payload.TryGetProperty(name, out JsonElement element)
            && element.ValueKind == JsonValueKind.String
            && Guid.TryParse(element.GetString(), out value);
    }
}
