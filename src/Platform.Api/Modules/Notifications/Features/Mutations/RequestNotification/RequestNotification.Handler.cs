using System.Diagnostics;
using System.Text.Json;
using NotificationHub.Api.Infrastructure.Messaging;
using NotificationHub.Api.Modules.Audit.Integration.V1;
using NotificationHub.Api.Modules.Notifications.Domain;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Auditing;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Idempotency;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Persistence;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Privacy;
using NotificationHub.Api.Modules.Notifications.Infrastructure.RateLimiting;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Templates;
using NotificationHub.Api.Modules.TemplateManagement.Integration.V1;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.Notifications.Features.Mutations;

internal static partial class RequestNotification
{
    private const string ReasonClassNotAllowed = "class-not-allowed-for-principal";
    private const string ReasonRecipientRateLimited = "recipient-rate-limited";
    private const string OutboxMessageType = "notification.accepted";
    private const string AuthenticationPurpose = "authentication";
    private const string AuthenticationDestination = "core-auth";

    internal sealed class Handler(
        PublishedTemplateGate templateGate,
        IngestionRateLimiter rateLimiter,
        IdempotencyFastPath idempotencyFastPath,
        VariablesProtector variablesProtector,
        IngestionWriter writer,
        TimeProvider timeProvider,
        ILogger<Handler> logger)
    {
        public async Task<Result<Outcome>> HandleAsync(
            Command command,
            string producer,
            IReadOnlySet<string> producerRoles,
            string idempotencyKey,
            CancellationToken cancellationToken)
        {
            var canonicalClass = command.Class;
            if (!producerRoles.Contains(NotificationClasses.RequiredRole(canonicalClass)))
            {
                await writer.AppendStandaloneAuditAsync(
                    RejectionEntry(command, producer, idempotencyKey, ReasonClassNotAllowed),
                    cancellationToken);
                logger.IngressRejected(command.Application, command.TemplateKey, canonicalClass, ReasonClassNotAllowed);
                return Result.Success<Outcome>(new Outcome.ClassNotAllowed(canonicalClass));
            }

            var payloadHash = ComputePayloadHash(command);

            RememberedAcceptance? remembered =
                await idempotencyFastPath.FindAsync(command.Application, idempotencyKey, cancellationToken);
            if (remembered is { } acceptance)
            {
                return await ResolveReplayAsync(
                    command, producer, payloadHash,
                    acceptance.NotificationId, acceptance.PayloadHash, cancellationToken);
            }

            RateLimitDecision rateDecision = await rateLimiter.EvaluateAsync(
                producer, command.Application, command.RecipientId, canonicalClass, cancellationToken);
            if (!rateDecision.Allowed)
            {
                if (rateDecision.Dimension == RateLimitedDimension.Recipient)
                {
                    await writer.AppendStandaloneAuditAsync(
                        RecipientRateLimitedEntry(command, producer, idempotencyKey),
                        cancellationToken);
                }

                logger.RateLimitedAtIngress(
                    command.Application, canonicalClass, rateDecision.Dimension, rateDecision.RetryAfterSeconds);
                return Result.Success<Outcome>(new Outcome.RateLimited(rateDecision.RetryAfterSeconds));
            }

            TemplateGateOutcome gate = await templateGate.EvaluateAsync(
                command.Application, command.TemplateKey, canonicalClass,
                NormalizedVariables(command), cancellationToken);
            if (gate is TemplateGateOutcome.Rejected rejection)
            {
                await writer.AppendStandaloneAuditAsync(
                    RejectionEntry(command, producer, idempotencyKey, rejection.Reason),
                    cancellationToken);
                logger.IngressRejected(command.Application, command.TemplateKey, canonicalClass, rejection.Reason);
                return Result.Success<Outcome>(
                    new Outcome.TemplateRejected(rejection.Reason, rejection.Detail, rejection.Checks));
            }

            PublishedTemplate template = ((TemplateGateOutcome.Approved)gate).Template;
            ProtectedVariables protectedVariables = await variablesProtector.ProtectAsync(
                command.Application, NormalizedVariables(command), template.SensitiveVariables, cancellationToken);

            DateTimeOffset acceptedAt = timeProvider.GetUtcNow();
            var notification = Notification.Accept(new NotificationDraft
            {
                Application = command.Application,
                IdempotencyKey = idempotencyKey,
                RecipientId = command.RecipientId,
                Class = canonicalClass,
                TemplateKey = command.TemplateKey,
                TemplateVersion = template.Version,
                VariablesMaskedJson = protectedVariables.MaskedJson,
                VariablesEncrypted = protectedVariables.Encrypted,
                CorrelationId = command.CorrelationId,
                RequestedBy = producer,
                TtlSeconds = command.TtlSeconds,
                ScheduledAt = command.ScheduledAt,
                AcceptedAt = acceptedAt,
            });
            var registration = IdempotencyRegistration.Register(
                command.Application, idempotencyKey, payloadHash, notification.Id, acceptedAt);

            PersistOutcome persisted = await writer.PersistAcceptedAsync(
                notification,
                registration,
                BuildOutboxMessage(notification, template, acceptedAt),
                AcceptedEntry(notification, producer, template.Version),
                cancellationToken);
            if (persisted is PersistOutcome.ExistingRegistration existing)
            {
                return await ResolveReplayAsync(
                    command, producer, payloadHash,
                    existing.NotificationId, existing.PayloadHash, cancellationToken);
            }

            await idempotencyFastPath.RememberAsync(
                command.Application,
                idempotencyKey,
                new RememberedAcceptance(notification.Id, payloadHash),
                cancellationToken);
            logger.NotificationAccepted(
                notification.Id, command.Application, canonicalClass, command.TemplateKey, template.Version);
            return Result.Success<Outcome>(new Outcome.Accepted(notification.Id));
        }

        /// <summary>
        /// Answers a replay from the authoritative registration: same payload
        /// hash replays the original id, a different one is a conflict.
        /// </summary>
        private async Task<Result<Outcome>> ResolveReplayAsync(
            Command command,
            string producer,
            string payloadHash,
            Guid existingNotificationId,
            string existingPayloadHash,
            CancellationToken cancellationToken)
        {
            if (!string.Equals(existingPayloadHash, payloadHash, StringComparison.Ordinal))
            {
                logger.IdempotencyConflictDetected(command.Application, command.TemplateKey);
                return Result.Success<Outcome>(new Outcome.IdempotencyConflict());
            }

            await writer.AppendStandaloneAuditAsync(
                DuplicateEntry(command, producer, existingNotificationId),
                cancellationToken);
            logger.NotificationReplayed(existingNotificationId, command.Application, command.TemplateKey);
            return Result.Success<Outcome>(new Outcome.Replayed(existingNotificationId));
        }

        private AuditEntry RejectionEntry(
            Command command,
            string producer,
            string idempotencyKey,
            string reason) => new()
        {
            ActorType = IngestionAuditVocabulary.ActorTypeProducer,
            ActorId = producer,
            Application = command.Application,
            Action = IngestionAuditVocabulary.NotificationRejectedAtIngress,
            EntityType = IngestionAuditVocabulary.EntityTypeNotification,
            EntityId = $"{command.Application}:{idempotencyKey}",
            DetailsJson = JsonSerializer.Serialize(new
            {
                source = IngestionAuditVocabulary.SourceRest,
                reason,
                @class = command.Class,
                templateKey = command.TemplateKey,
            }),
            OccurredAt = timeProvider.GetUtcNow(),
        };

        private AuditEntry RecipientRateLimitedEntry(
            Command command,
            string producer,
            string idempotencyKey) => new()
        {
            ActorType = IngestionAuditVocabulary.ActorTypeProducer,
            ActorId = producer,
            Application = command.Application,
            Action = IngestionAuditVocabulary.NotificationRejectedAtIngress,
            EntityType = IngestionAuditVocabulary.EntityTypeNotification,
            EntityId = $"{command.Application}:{idempotencyKey}",
            DetailsJson = JsonSerializer.Serialize(new
            {
                source = IngestionAuditVocabulary.SourceRest,
                reason = ReasonRecipientRateLimited,
                @class = command.Class,
                templateKey = command.TemplateKey,
                recipientId = command.RecipientId,
            }),
            OccurredAt = timeProvider.GetUtcNow(),
        };

        private AuditEntry DuplicateEntry(
            Command command,
            string producer,
            Guid existingNotificationId) => new()
        {
            ActorType = IngestionAuditVocabulary.ActorTypeProducer,
            ActorId = producer,
            Application = command.Application,
            Action = IngestionAuditVocabulary.NotificationDuplicate,
            EntityType = IngestionAuditVocabulary.EntityTypeNotification,
            EntityId = existingNotificationId.ToString(),
            DetailsJson = JsonSerializer.Serialize(new
            {
                source = IngestionAuditVocabulary.SourceRest,
                @class = command.Class,
                templateKey = command.TemplateKey,
            }),
            OccurredAt = timeProvider.GetUtcNow(),
        };

        private AuditEntry AcceptedEntry(
            Notification notification,
            string producer,
            int templateVersion) => new()
        {
            ActorType = IngestionAuditVocabulary.ActorTypeProducer,
            ActorId = producer,
            Application = notification.Application,
            Action = IngestionAuditVocabulary.NotificationAccepted,
            EntityType = IngestionAuditVocabulary.EntityTypeNotification,
            EntityId = notification.Id.ToString(),
            DetailsJson = JsonSerializer.Serialize(new
            {
                source = IngestionAuditVocabulary.SourceRest,
                @class = notification.Class,
                templateKey = notification.TemplateKey,
                templateVersion,
                idempotencyKey = notification.IdempotencyKey,
            }),
            OccurredAt = timeProvider.GetUtcNow(),
        };

        private static OutboxAppend BuildOutboxMessage(
            Notification notification,
            PublishedTemplate template,
            DateTimeOffset acceptedAt)
        {
            var destination = string.Equals(template.Purpose, AuthenticationPurpose, StringComparison.Ordinal)
                ? AuthenticationDestination
                : $"core-{notification.Class}";
            var traceparent = Activity.Current?.Id;
            return new OutboxAppend
            {
                Destination = destination,
                EventType = OutboxMessageType,
                MessageKey = notification.RecipientId,
                HeadersJson = traceparent is null
                    ? "{}"
                    : JsonSerializer.Serialize(new { traceparent }),
                PayloadJson = JsonSerializer.Serialize(new
                {
                    messageId = Guid.CreateVersion7(),
                    type = OutboxMessageType,
                    schemaVersion = 1,
                    occurredAt = acceptedAt,
                    traceparent,
                    priorityClass = notification.Class,
                    payload = new { notificationId = notification.Id },
                }),
                PriorityClass = notification.Class,
            };
        }

        private static JsonElement? NormalizedVariables(Command command)
            => command.Variables is { ValueKind: JsonValueKind.Object } variables ? variables : null;
    }
}
