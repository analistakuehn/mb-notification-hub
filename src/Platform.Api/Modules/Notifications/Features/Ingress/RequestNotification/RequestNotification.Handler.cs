using System.Diagnostics;
using System.Text.Json;
using FluentValidation;
using FluentValidation.Results;
using NotificationHub.Api.Infrastructure.Messaging;
using NotificationHub.Api.Modules.AttachmentManagement.Integration.V1;
using NotificationHub.Api.Modules.Audit.Integration.V1;
using NotificationHub.Api.Modules.Notifications.Domain;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Auditing;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Authorization;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Events;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Http;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Idempotency;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Persistence;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Privacy;
using NotificationHub.Api.Modules.Notifications.Infrastructure.RateLimiting;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Templates;
using NotificationHub.Api.Modules.Notifications.Integration.V1;
using NotificationHub.Api.Modules.TemplateManagement.Integration.V1;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.Notifications.Features.Ingress.RequestNotification;

internal static partial class RequestNotification
{
    private const string OutboxMessageType = "notification.accepted";
    private const string AuthenticationDestination = "core-auth";

    /// <summary>
    /// One registration that already answers this idempotency key, together
    /// with the hash of the request being resolved against it.
    /// </summary>
    private sealed record ReplayCandidate(
        string IdempotencyKey,
        string PayloadHash,
        Guid NotificationId,
        string StoredPayloadHash);

    /// <summary>
    /// The ingestion use case, neutral to the transport that carried the
    /// request. It receives an authorization question already answered and the
    /// origin of the request, and it answers with data: every rejection is a
    /// legitimate outcome, never an exception, so a synchronous caller maps it
    /// to a problem response and an asynchronous one maps it to a dead-letter
    /// record without either of them re-implementing a single rule.
    /// </summary>
    internal sealed class Handler(
        IValidator<Command> validator,
        PublishedTemplateGate templateGate,
        IIngressAdmission admission,
        VariablesProtector variablesProtector,
        IIngestionSink sink,
        TimeProvider timeProvider,
        ILogger<Handler> logger)
    {
        /// <summary>
        /// Worker composition for the bus path, whose producer kill switch is
        /// evaluated before the shared use case is invoked.
        /// </summary>
        public Handler(
            IValidator<Command> validator,
            PublishedTemplateGate templateGate,
            IngressControls controls,
            VariablesProtector variablesProtector,
            IIngestionSink sink,
            TimeProvider timeProvider,
            ILogger<Handler> logger)
            : this(
                validator,
                templateGate,
                IngressAdmission.ForBus(controls),
                variablesProtector,
                sink,
                timeProvider,
                logger)
        {
        }

        public async Task<Result<Outcome>> HandleAsync(
            Command command,
            string producer,
            ProducerAuthorization authorization,
            IngestionOrigin origin,
            string idempotencyKey,
            CancellationToken cancellationToken)
        {
            // Shape first, always: an unreadable request is answered for what
            // it is, even when the producer would also fail authorization.
            ValidationResult validation = await validator.ValidateAsync(command, cancellationToken);
            if (!validation.IsValid)
            {
                await RejectAsync(
                    command, producer, origin, idempotencyKey,
                    NotificationRejectionReasons.PayloadInvalid, cancellationToken);
                return Result.Success<Outcome>(
                    new Outcome.PayloadInvalid(validation.ToDictionary().AsReadOnly()));
            }

            var canonicalClass = command.Class;
            if (authorization is ProducerAuthorization.Denied denial)
            {
                await RejectAsync(
                    command, producer, origin, idempotencyKey, denial.Reason, cancellationToken);
                return Result.Success<Outcome>(new Outcome.ProducerNotAuthorized(denial.Reason));
            }

            var payloadHash = ComputePayloadHash(command);

            AdmissionDecision admissionDecision = await admission.EvaluateAsync(
                command,
                producer,
                origin,
                idempotencyKey,
                cancellationToken);
            if (admissionDecision is AdmissionDecision.Replay replay)
            {
                return await ResolveReplayAsync(
                    command,
                    producer,
                    origin,
                    new ReplayCandidate(
                        idempotencyKey,
                        payloadHash,
                        replay.Acceptance.NotificationId,
                        replay.Acceptance.PayloadHash),
                    cancellationToken);
            }

            if (admissionDecision is AdmissionDecision.ProducerDisabled)
            {
                await RejectAsync(
                    command,
                    producer,
                    origin,
                    idempotencyKey,
                    NotificationRejectionReasons.ProducerDisabled,
                    cancellationToken);
                return Result.Success<Outcome>(new Outcome.ProducerDisabled());
            }

            if (admissionDecision is AdmissionDecision.KillSwitchUnavailable) return Result.Success<Outcome>(new Outcome.KillSwitchUnavailable());

            if (admissionDecision is AdmissionDecision.RateLimited limited)
            {
                // The principal dimension never records a trail: under the
                // pressure it exists to absorb, one audit row and one event per
                // refused request is the storm the control was meant to stop.
                if (limited.Decision.Dimension == RateLimitedDimension.Recipient)
                {
                    await sink.RecordTrailAsync(
                        RecipientRateLimitedEntry(command, producer, origin, idempotencyKey),
                        RejectionEvent(
                            command, idempotencyKey, NotificationRejectionReasons.RecipientRateLimited),
                        cancellationToken);
                }

                logger.RateLimitedAtIngress(
                    command.Application,
                    canonicalClass,
                    limited.Decision.Dimension,
                    limited.Decision.RetryAfterSeconds);
                return Result.Success<Outcome>(
                    new Outcome.RateLimited(
                        limited.Decision.Dimension,
                        limited.Decision.RetryAfterSeconds));
            }

            if (admissionDecision is not AdmissionDecision.Allowed)
            {
                throw new InvalidOperationException(
                    $"Decisão de admissão desconhecida: {admissionDecision.GetType().Name}.");
            }

            TemplateGateOutcome gate = await templateGate.EvaluateAsync(
                command.Application, command.TemplateKey, canonicalClass,
                NormalizedVariables(command),
                allowSensitiveVariables: origin.Source == IngestionSource.Rest,
                cancellationToken);
            if (gate is TemplateGateOutcome.Rejected rejection)
            {
                await RejectAsync(
                    command, producer, origin, idempotencyKey, rejection.Reason, cancellationToken);
                return Result.Success<Outcome>(TemplateRejectionOutcome(rejection));
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
                AuthFlow = IsAuthenticationFlow(template),
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

            PersistOutcome persisted = await sink.PersistAcceptedAsync(
                notification,
                registration,
                BuildOutboxMessage(notification, acceptedAt),
                AcceptedEntry(notification, producer, origin, template.Version),
                ClaimOf(command, notification, idempotencyKey),
                cancellationToken);
            if (persisted is PersistOutcome.AttachmentsRefused refused)
            {
                return await ResolveAttachmentRefusalAsync(
                    command, producer, origin, idempotencyKey, refused.Status, cancellationToken);
            }

            if (persisted is PersistOutcome.ExistingRegistration existing)
            {
                return await ResolveReplayAsync(
                    command,
                    producer,
                    origin,
                    new ReplayCandidate(
                        idempotencyKey, payloadHash, existing.NotificationId, existing.PayloadHash),
                    cancellationToken);
            }

            await admission.RememberAsync(
                command.Application,
                idempotencyKey,
                new RememberedAcceptance(notification.Id, payloadHash),
                cancellationToken);
            logger.NotificationAccepted(
                notification.Id, command.Application, canonicalClass, command.TemplateKey, template.Version);
            return Result.Success<Outcome>(new Outcome.Accepted(notification.Id));
        }

        /// <summary>
        /// The outcome a catalog rejection maps to. The bus restriction is its
        /// own outcome because its dead-letter record must name the declared
        /// variables and must never carry their values.
        /// </summary>
        private static Outcome TemplateRejectionOutcome(TemplateGateOutcome.Rejected rejection)
            => rejection.Reason == TemplateGateReasons.SensitiveVariablesOnBus
                ? new Outcome.SensitiveVariablesOnBus(rejection.SensitiveVariables ?? [])
                : new Outcome.TemplateRejected(rejection.Reason, rejection.Detail, rejection.Checks);

        /// <summary>
        /// The claim this request asks for, or nothing when it names no
        /// attachments. The key is the producer's own idempotency key, not the
        /// identifier of the notification: a retry of an acceptance whose
        /// commit was never confirmed mints a new notification for the same
        /// request, and a claim keyed on it would take a second hold over the
        /// same attachments for one acceptance.
        /// </summary>
        private static AttachmentClaimRequest? ClaimOf(
            Command command,
            Notification notification,
            string idempotencyKey)
            => command.Attachments is { Count: > 0 } references
                ? new AttachmentClaimRequest
                {
                    NotificationId = notification.Id,
                    Application = command.Application,
                    ClaimKey = idempotencyKey,
                    References = AttachmentReferences.Of(references),
                }
                : null;

        /// <summary>
        /// Answers a claim that refused. A key that already stands for a
        /// different set is the same fact the idempotency contract already has
        /// a word for, so it answers with that word; anything else is a set
        /// this request may not have, which is its own answer.
        /// </summary>
        private async Task<Result<Outcome>> ResolveAttachmentRefusalAsync(
            Command command,
            string producer,
            IngestionOrigin origin,
            string idempotencyKey,
            AttachmentClaimStatus status,
            CancellationToken cancellationToken)
        {
            if (status == AttachmentClaimStatus.ClaimKeyConflict)
            {
                await RejectAsync(
                    command, producer, origin, idempotencyKey,
                    NotificationRejectionReasons.IdempotencyKeyConflict, cancellationToken);
                logger.IdempotencyConflictDetected(command.Application, command.TemplateKey);
                return Result.Success<Outcome>(new Outcome.IdempotencyConflict());
            }

            // The trail is recorded and the bus stays quiet. The reason is not
            // a member of the published rejection catalog yet, and a rejection
            // event carrying a word no producer can look up is worse than no
            // event: the synchronous answer already names it.
            await sink.RecordTrailAsync(
                RejectionEntry(
                    command, producer, origin, idempotencyKey,
                    IngestionProblems.AttachmentsNotClaimableType),
                integrationEvent: null,
                cancellationToken);
            logger.IngressRejected(
                command.Application,
                command.TemplateKey,
                command.Class,
                IngestionProblems.AttachmentsNotClaimableType);
            return Result.Success<Outcome>(new Outcome.AttachmentsNotClaimable());
        }

        /// <summary>Records the trail of one rejection: the audit event and the outgoing rejection event.</summary>
        private async Task RejectAsync(
            Command command,
            string producer,
            IngestionOrigin origin,
            string idempotencyKey,
            string reason,
            CancellationToken cancellationToken)
        {
            await sink.RecordTrailAsync(
                RejectionEntry(command, producer, origin, idempotencyKey, reason),
                RejectionEvent(command, idempotencyKey, reason),
                cancellationToken);
            logger.IngressRejected(command.Application, command.TemplateKey, command.Class, reason);
        }

        /// <summary>
        /// Answers a replay from the authoritative registration: same payload
        /// hash replays the original id, a different one is a conflict.
        /// </summary>
        private async Task<Result<Outcome>> ResolveReplayAsync(
            Command command,
            string producer,
            IngestionOrigin origin,
            ReplayCandidate candidate,
            CancellationToken cancellationToken)
        {
            if (!string.Equals(candidate.StoredPayloadHash, candidate.PayloadHash, StringComparison.Ordinal))
            {
                await RejectAsync(
                    command, producer, origin, candidate.IdempotencyKey,
                    NotificationRejectionReasons.IdempotencyKeyConflict, cancellationToken);
                logger.IdempotencyConflictDetected(command.Application, command.TemplateKey);
                return Result.Success<Outcome>(new Outcome.IdempotencyConflict());
            }

            // A replay is not a result: it repeats an answer the producer
            // already received, so the trail records it and the bus stays quiet.
            await sink.RecordTrailAsync(
                DuplicateEntry(command, producer, origin, candidate.NotificationId),
                integrationEvent: null,
                cancellationToken);
            logger.NotificationReplayed(candidate.NotificationId, command.Application, command.TemplateKey);
            return Result.Success<Outcome>(new Outcome.Replayed(candidate.NotificationId));
        }

        /// <summary>
        /// The outgoing rejection event, or null when the request carries no
        /// subject to key it by. A malformed payload without a recipient has
        /// nothing the bus contract can address; its diagnosis travels on the
        /// dead-letter record instead.
        /// </summary>
        private OutboxAppend? RejectionEvent(Command command, string? idempotencyKey, string reason)
            => string.IsNullOrWhiteSpace(command.RecipientId)
                ? null
                : NotificationEvents.Rejected(new NotificationRejected
                {
                    RecipientId = command.RecipientId,
                    Class = command.Class,
                    TemplateKey = command.TemplateKey,
                    Reason = reason,
                    IdempotencyKey = idempotencyKey,
                    CorrelationId = command.CorrelationId,
                    OccurredAt = timeProvider.GetUtcNow(),
                    Traceparent = Activity.Current?.Id,
                });

        private AuditEntry RejectionEntry(
            Command command,
            string producer,
            IngestionOrigin origin,
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
                source = IngestionAuditVocabulary.SourceOf(origin.Source),
                origin = origin.Coordinates(),
                reason,
                @class = command.Class,
                templateKey = command.TemplateKey,
            }),
            OccurredAt = timeProvider.GetUtcNow(),
        };

        private AuditEntry RecipientRateLimitedEntry(
            Command command,
            string producer,
            IngestionOrigin origin,
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
                source = IngestionAuditVocabulary.SourceOf(origin.Source),
                origin = origin.Coordinates(),
                reason = NotificationRejectionReasons.RecipientRateLimited,
                @class = command.Class,
                templateKey = command.TemplateKey,
                recipientId = command.RecipientId,
            }),
            OccurredAt = timeProvider.GetUtcNow(),
        };

        private AuditEntry DuplicateEntry(
            Command command,
            string producer,
            IngestionOrigin origin,
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
                source = IngestionAuditVocabulary.SourceOf(origin.Source),
                origin = origin.Coordinates(),
                @class = command.Class,
                templateKey = command.TemplateKey,
            }),
            OccurredAt = timeProvider.GetUtcNow(),
        };

        private AuditEntry AcceptedEntry(
            Notification notification,
            string producer,
            IngestionOrigin origin,
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
                source = IngestionAuditVocabulary.SourceOf(origin.Source),
                origin = origin.Coordinates(),
                @class = notification.Class,
                templateKey = notification.TemplateKey,
                templateVersion,
                idempotencyKey = notification.IdempotencyKey,
            }),
            OccurredAt = timeProvider.GetUtcNow(),
        };

        /// <summary>
        /// Whether this template's purpose puts the notification in an
        /// authentication flow. Read here, once, because this is the only
        /// point of the lifecycle that already holds the published template:
        /// every later producer reads the stored answer instead.
        /// </summary>
        private static bool IsAuthenticationFlow(PublishedTemplate template)
            => TemplatePurposes.IsAuthentication(template.Purpose);

        private static OutboxAppend BuildOutboxMessage(
            Notification notification,
            DateTimeOffset acceptedAt)
        {
            var destination = notification.AuthFlow
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
