using NotificationHub.Api.Modules.Dispatch.Integration.V1;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Persistence;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.Notifications.Features.DeliveryTracking.Webhooks;

internal static partial class ReceiveProviderWebhook
{
    /// <summary>
    /// One proven callback plus the correlation its route carried. The
    /// callback arrives already verified: this use case never sees unproven
    /// bytes, which is what lets it do no authentication of its own.
    /// </summary>
    internal sealed record Command(
        VerifiedProviderWebhook Webhook,
        DispatchCorrelation? RouteCorrelation);

    /// <summary>
    /// Turns one proven callback into stored evidence and one queue message
    /// per event. The work is deliberately small and bounded: translate,
    /// deduplicate, store, announce. Resolving which attempt an event
    /// describes, deciding whether the state machine accepts it and writing
    /// the trail all happen off the request.
    /// </summary>
    internal sealed class Handler(
        IProviderWebhookInterpreterResolver resolver,
        DeliveryEventWriter writer,
        ILogger<Handler> logger)
    {
        public async Task<Result<Receipt>> HandleAsync(
            Command command,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            Result<IProviderWebhookInterpreter> interpreter =
                resolver.Resolve(command.Webhook.ProviderKey);
            if (interpreter.IsFailure)
            {
                return new Result<Receipt>(
                    false, default, interpreter.ErrorKind, interpreter.Error);
            }

            Result<IReadOnlyList<ProviderDeliveryEvent>> translated =
                interpreter.Value!.Interpret(command.Webhook);
            if (translated.IsFailure)
            {
                logger.DeliveryWebhookUnreadable(
                    command.Webhook.ProviderKey, translated.Error ?? ProviderWebhookRefusal.PayloadUnreadable);
                return new Result<Receipt>(false, default, translated.ErrorKind, translated.Error);
            }

            IReadOnlyList<ProviderDeliveryEvent> events = translated.Value!;
            if (events.Count == 0)
            {
                // A success with nothing to store. A batch made only of events
                // this hub does not track is ordinary traffic, and answering
                // an error would buy an endless redelivery of a callback that
                // never had anything in it for us.
                logger.DeliveryWebhookEmptyBatch(command.Webhook.ProviderKey);
                return Result.Success(new Receipt(0, 0, 0));
            }

            // Sealed once for the whole callback: the evidence of every event
            // in a batch is the batch itself, and the cipher call is the most
            // expensive step of the request.
            var sealedPayload = await writer.SealPayloadAsync(command.Webhook.Body, cancellationToken);

            var stored = 0;
            var duplicated = 0;
            foreach (ProviderDeliveryEvent providerEvent in events)
            {
                DeliveryEventRecordOutcome outcome = await writer.RecordAsync(
                    providerEvent,
                    providerEvent.Correlation ?? command.RouteCorrelation,
                    sealedPayload,
                    cancellationToken);
                if (outcome == DeliveryEventRecordOutcome.Stored)
                {
                    stored++;
                }
                else
                {
                    duplicated++;
                    logger.DeliveryWebhookEventDuplicated(providerEvent.ProviderKey);
                }
            }

            logger.DeliveryWebhookAccepted(command.Webhook.ProviderKey, events.Count, stored, duplicated);
            return Result.Success(new Receipt(events.Count, stored, duplicated));
        }
    }
}
