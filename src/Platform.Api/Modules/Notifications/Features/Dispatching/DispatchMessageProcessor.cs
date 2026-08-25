using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NotificationHub.Api.Infrastructure.Cryptography;
using NotificationHub.Api.Infrastructure.Messaging.Consuming;
using NotificationHub.Api.Modules.ContactConsent.Integration.V1;
using NotificationHub.Api.Modules.Dispatch.Integration.V1;
using NotificationHub.Api.Modules.Notifications.Domain;
using NotificationHub.Api.Modules.Notifications.Features.KillSwitch;
using NotificationHub.Api.Modules.Notifications.Infrastructure.KillSwitch;
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
    ChannelKillSwitchGate channelKillSwitchGate,
    AutomaticChannelKillSwitch automaticChannelKillSwitch,
    IRecipientDirectory recipientDirectory,
    IDeviceTokenLifecycle deviceTokenLifecycle,
    IEnvelopeCipher cipher,
    TimeProvider timeProvider,
    ILogger<DispatchMessageProcessor> logger) : ISqsMessageProcessor
{
    internal const int SupportedSchemaVersion = DispatchMessages.SchemaVersion;
    internal const string ReasonPayloadWithoutIds = "payload-missing-attempt-reference";
    internal const string ReasonAttemptNotFound = "attempt-not-found";
    internal const string ReasonNotificationNotFound = "notification-not-found";
    internal const string ReasonProviderThrottled = "provider-throttled";
    internal const string ReasonCircuitOpen = "circuit-open";

    /// <summary>
    /// Stable reason of a send this hub held back to stay inside the
    /// provider's contracted rate. It is separate from the provider's own
    /// throttle because the two read differently on a queue: this one is
    /// congestion of our own making and says nothing about the provider.
    /// </summary>
    internal const string ReasonRateLimited = "rate-limited";

    /// <summary>Stable code of a send whose contact point vanished between routing and send.</summary>
    internal const string ErrorContactPointUnavailable = "contact-point-unavailable";

    /// <summary>Stable code of a send whose device token was invalidated between claim and send.</summary>
    internal const string ErrorDeviceTokenInactive = "device-token-inactive";

    /// <summary>
    /// Stable code of an attempt whose notification ran out of validity before
    /// the send. It names the notification and not the attempt on purpose: the
    /// attempt is healthy, and what ended is the window in which delivering it
    /// still meant something.
    /// </summary>
    internal const string ErrorNotificationExpired = "notification-expired";

    /// <summary>Adapter code of an open circuit: the only transient error that proves no call was taken.</summary>
    internal const string CircuitOpenErrorCode = "circuit-open";

    /// <summary>
    /// Code the provider surface returns when this hub's own rate limit held
    /// the send back. Mirrored here as a string, like the open-circuit code
    /// above: the code is part of what the send contract answers, and the
    /// types behind it belong to another context.
    /// </summary>
    internal const string RateLimitedErrorCode = "rate-limited";

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
            .FirstOrDefaultAsync(
                candidate => candidate.Id == attemptId && candidate.NotificationId == notificationId,
                cancellationToken);
        if (attempt is null)
        {
            // The outbox commits with the attempt, so an absent row means the
            // claim check outlived its state: permanently unprocessable.
            return new MessageDisposition.Discard(ReasonAttemptNotFound);
        }

        Notification? notification = await db.Notifications
            .FirstOrDefaultAsync(candidate => candidate.Id == notificationId, cancellationToken);
        if (notification is null) return new MessageDisposition.Discard(ReasonNotificationNotFound);

        if (attempt.Status != NotificationAttemptStatuses.Queued)
        {
            // Redelivery after a claim or a verdict: the stored status is the
            // authority and the send never repeats. An attempt parked on
            // sending by a crash stays for reconciliation, never a resend.
            logger.DispatchDuplicateSkipped(attempt.Id, attempt.Status);
            return new MessageDisposition.Duplicate();
        }

        Result<Channel> channel = Channel.Create(attempt.Channel);
        if (channel.IsFailure) return new MessageDisposition.Discard($"channel-unknown:{attempt.Channel}");

        MessageDisposition? stopped = await channelKillSwitchGate.EvaluateAsync(
            notification, attempt, envelope, claimed: false, cancellationToken);
        if (stopped is not null) return stopped;

        Result<IChannelProvider> provider = await channelKillSwitchGate.ResolveProviderAsync(
            channel.Value!, cancellationToken);
        if (provider.IsFailure)
        {
            // Configuration or deployment defect: transient by contract, the
            // message returns with backoff and heals without loss.
            throw new InvalidOperationException(provider.Error);
        }

        var isPush = string.Equals(
            attempt.Channel, AttemptDispatchWriter.PushChannel, StringComparison.Ordinal);
        var isSms = string.Equals(
            attempt.Channel, Channel.Sms.Value, StringComparison.Ordinal);
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

        // The validity that is left decides whether this send is worth making
        // at all. It is measured after the claim, so the answer describes the
        // instant of the call and not the instant the message was written, and
        // before the destination is revealed, because a message nobody will
        // read anymore is not worth a plaintext contact in memory.
        TimeSpan remainingValidity = notification.ExpiresAt - timeProvider.GetUtcNow();
        if (remainingValidity <= TimeSpan.Zero)
        {
            var expired = await writer.RecordFailureAsync(
                attempt, notification, ErrorNotificationExpired, envelope.MessageId, cancellationToken);
            if (expired) logger.DispatchAttemptExpired(attempt.Id, notification.Id, attempt.Channel);

            return expired ? new MessageDisposition.Processed() : new MessageDisposition.Duplicate();
        }

        Result<DeliveryTarget> target = await ResolveTargetAsync(
            notification, attempt, isPush, isSms, deviceTokenId, cancellationToken);
        if (target.IsFailure)
        {
            var errorCode = isPush ? ErrorDeviceTokenInactive : ErrorContactPointUnavailable;
            var settled = await writer.RecordFailureAsync(
                attempt, notification, errorCode, envelope.MessageId, cancellationToken);
            if (settled) logger.DispatchAttemptFailed(attempt.Id, notification.Id, errorCode);

            return settled ? new MessageDisposition.Processed() : new MessageDisposition.Duplicate();
        }

        StoredAttemptContent content = await StoredAttemptContent.OpenAsync(
            cipher, notification.Application, attempt.RenderedContentEncrypted, cancellationToken);
        var request = new DispatchRequest(
            target.Value!,
            content.ToRenderedMessage(),
            new DispatchCorrelation(notification.Id, attempt.Id),
            remainingValidity,
            notification.Application);

        stopped = await channelKillSwitchGate.EvaluateAsync(
            notification, attempt, envelope, claimed: true, cancellationToken);
        if (stopped is not null) return stopped;

        // The provider surface spends this send's share of the contracted rate
        // before it calls, which is why the send happens here and not earlier:
        // budget is only worth spending on a message that is still valid and
        // still allowed to leave.
        ProviderResult result = await provider.Value!.SendAsync(request, cancellationToken);
        var settlement = new DispatchSettlementContext(
            notification,
            attempt,
            provider.Value!.ProviderKey,
            deviceTokenId,
            envelope.MessageId);
        MessageDisposition disposition = await SettleVerdictAsync(settlement, result, cancellationToken);

        // After the settlement, and never inside it: what one verdict says
        // about the provider's circuit is a channel-wide matter, and it must
        // not be able to undo the transition of this attempt.
        await automaticChannelKillSwitch.ObserveAsync(
            attempt.Channel, CircuitSignalOf(result), cancellationToken);
        return disposition;
    }

    /// <summary>
    /// Whether the provider's acceptance of this attempt is itself the
    /// delivery of the notification. Only push says yes, because it is the
    /// channel whose provider reports nothing after taking the message, and
    /// only on the last step of the plan: a stamped fallback deadline is
    /// exactly the proof that a later step exists, so declaring delivery on
    /// acceptance there would end the notification and the step that was meant
    /// to rescue it would never run.
    /// </summary>
    internal static bool DeliversOnAcceptance(NotificationAttempt attempt)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        return string.Equals(
                attempt.Channel, AttemptDispatchWriter.PushChannel, StringComparison.Ordinal)
            && attempt.FallbackDeadline is null;
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

    /// <summary>
    /// What one verdict says about the provider circuit of this channel. Only
    /// the open circuit counts as a circuit signal, because it is the only
    /// answer the pipeline gives without calling: every other verdict, an
    /// acceptance, a rejection, a timeout, a throttle by the provider, proves
    /// the breaker let the call through and therefore that the circuit is
    /// closed. A send this hub held back on its own rate says nothing at all
    /// about the provider, so it neither opens nor closes a window.
    /// </summary>
    internal static ChannelCircuitSignal CircuitSignalOf(ProviderResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.Outcome switch
        {
            ProviderOutcome.TransientError when string.Equals(
                result.ErrorCode, CircuitOpenErrorCode, StringComparison.Ordinal) =>
                ChannelCircuitSignal.CircuitOpen,
            ProviderOutcome.Throttled when string.Equals(
                result.ErrorCode, RateLimitedErrorCode, StringComparison.Ordinal) =>
                ChannelCircuitSignal.None,
            _ => ChannelCircuitSignal.ProviderAnswered,
        };
    }

    /// <summary>Why a send that never reached the provider goes back to the queue.</summary>
    private static string RequeueReason(ProviderResult result)
    {
        if (result.Outcome != ProviderOutcome.Throttled) return ReasonCircuitOpen;

        return string.Equals(result.ErrorCode, RateLimitedErrorCode, StringComparison.Ordinal)
            ? ReasonRateLimited
            : ReasonProviderThrottled;
    }

    private async Task<MessageDisposition> SettleVerdictAsync(
        DispatchSettlementContext context,
        ProviderResult result,
        CancellationToken cancellationToken)
    {
        switch (Decide(result))
        {
            case DispatchVerdict.Sent:
                var sent = await writer.RecordSentAsync(
                    context.Attempt, context.Notification, context.ProviderKey, result.ProviderMessageId,
                    context.MessageId,
                    deliveredOnAcceptance: DeliversOnAcceptance(context.Attempt),
                    cancellationToken);
                if (sent)
                {
                    logger.DispatchAttemptSent(
                        context.Attempt.Id, context.Notification.Id, context.ProviderKey);
                }

                return sent ? new MessageDisposition.Processed() : new MessageDisposition.Duplicate();
            case DispatchVerdict.Failed:
                var errorCode = result.ErrorCode ?? "provider-rejected";
                var failed = await writer.RecordFailureAsync(
                    context.Attempt, context.Notification, errorCode, context.MessageId, cancellationToken);
                if (failed)
                {
                    logger.DispatchAttemptFailed(context.Attempt.Id, context.Notification.Id, errorCode);
                    if (context.IsPush && context.DeviceTokenId is { } tokenId
                        && TokenInvalidationCodes.Contains(result.ErrorCode, StringComparer.Ordinal))
                    {
                        // After the verdict commit and outside its transaction
                        // on purpose: the invalidation is the owning module's
                        // own transactional write, idempotent on repetition.
                        await ReportDeadTokenAsync(
                            context.Notification.RecipientId,
                            tokenId,
                            result.ErrorCode!,
                            cancellationToken);
                    }
                }

                return failed ? new MessageDisposition.Processed() : new MessageDisposition.Duplicate();
            case DispatchVerdict.Requeue:
                await writer.RevertToQueuedAsync(context.Attempt, cancellationToken);
                var reason = RequeueReason(result);
                logger.DispatchAttemptRequeued(context.Attempt.Id, context.Notification.Id, reason);
                return new MessageDisposition.Postponed(result.RetryAfter, reason);
            case DispatchVerdict.Unknown:
                var parked = await writer.RecordUnknownAsync(
                    context.Attempt,
                    context.Notification,
                    result.ErrorCode,
                    context.MessageId,
                    cancellationToken);
                if (parked)
                {
                    logger.DispatchAttemptUnknown(
                        context.Attempt.Id, context.Notification.Id, result.ErrorCode);
                }

                return parked ? new MessageDisposition.Processed() : new MessageDisposition.Duplicate();
            default:
                throw new InvalidOperationException("Veredito de despacho não suportado.");
        }
    }

    private sealed record DispatchSettlementContext(
        Notification Notification,
        NotificationAttempt Attempt,
        string ProviderKey,
        Guid? DeviceTokenId,
        Guid MessageId)
    {
        internal bool IsPush => string.Equals(
            Attempt.Channel, AttemptDispatchWriter.PushChannel, StringComparison.Ordinal);
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
        if (snapshot.IsFailure) return [];

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
        bool isSms,
        Guid? deviceTokenId,
        CancellationToken cancellationToken)
    {
        if (isPush)
        {
            if (deviceTokenId is not { } tokenId) return Result.NotFound<DeliveryTarget>("O attempt de push não carrega um token de dispositivo.");

            Result<string> token = await recipientDirectory.RevealDeviceTokenAsync(
                notification.RecipientId, tokenId, cancellationToken);
            return token.IsFailure
                ? new Result<DeliveryTarget>(false, default, token.ErrorKind, token.Error)
                : Result.Success<DeliveryTarget>(new PushDeliveryTarget(token.Value!));
        }

        if (attempt.ContactPointId is not { } contactPointId) return Result.NotFound<DeliveryTarget>("O attempt não referencia um ponto de contato.");

        Result<string> value = await recipientDirectory.RevealContactValueAsync(
            notification.RecipientId, contactPointId, cancellationToken);
        return value.IsFailure
            ? new Result<DeliveryTarget>(false, default, value.ErrorKind, value.Error)
            : Result.Success<DeliveryTarget>(isSms
                ? new SmsDeliveryTarget(value.Value!)
                : new EmailDeliveryTarget(value.Value!));
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
            if (invalidated.IsFailure) logger.DispatchTokenInvalidationFailed(deviceTokenId, invalidated.Error ?? providerCode);
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
