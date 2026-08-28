using NotificationHub.Api.Modules.Audit.Integration.V1;
using NotificationHub.Api.Modules.Compliance.Infrastructure.Disclosure;
using NotificationHub.Api.Modules.Compliance.Infrastructure.Http;
using NotificationHub.Api.Modules.ContactConsent.Integration.V1;
using NotificationHub.Api.Modules.Notifications.Integration.V1;
using NotificationHub.Api.Modules.TemplateManagement.Integration.V1;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.Compliance.Features.Disclosure;

internal static partial class GetNotificationEvidence
{
    /// <summary>
    /// Composes the reconstruction from the published contracts of the four
    /// owning modules and records the disclosure before the answer exists as a
    /// response value. Every heavy read happens first on purpose: the append
    /// holds the chain advisory lock until it commits, so anything left inside
    /// that window would stretch the serialization of the whole partition.
    /// </summary>
    internal sealed class Handler(
        INotificationEvidence notifications,
        IAuditEvidence audit,
        IContactHistory contacts,
        IHistoricalCatalog catalog,
        DisclosureRecorder recorder,
        TimeProvider timeProvider,
        ILogger<Handler> logger)
    {
        public async Task<Result<Response>> HandleAsync(
            Guid notificationId,
            DisclosureActor actor,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(actor);

            Result<NotificationEvidence> found = await notifications.FindAsync(notificationId, cancellationToken);
            if (found.IsFailure)
            {
                return new Result<Response>(false, null, found.ErrorKind, found.Error);
            }

            NotificationEvidence evidence = found.Value!;
            DateTimeOffset composedAt = timeProvider.GetUtcNow();
            DateTimeOffset from = evidence.CreatedAt - AuditReadContract.EvidenceLookback;

            AuditSubjectLinks trail = await audit.ReadLinksAsync(
                Subjects(evidence), from, composedAt, cancellationToken);
            IReadOnlyList<ApprovalRecord> approvals = await audit.ReadApprovalsAsync(
                ApprovalSubjectTypes.TemplateVersion,
                evidence.TemplateKey,
                evidence.TemplateVersion,
                cancellationToken);

            Result<HistoricalTemplateVersion> template = await catalog.FindTemplateVersionAsync(
                evidence.Application, evidence.TemplateKey, evidence.TemplateVersion, cancellationToken);

            RecipientView recipient = await ReadRecipientAsync(evidence, from, composedAt, cancellationToken);

            var response = new Response
            {
                Id = NotificationIdentity.Format(evidence.Id),
                Disclosure = new DisclosureView
                {
                    ComposedAt = composedAt,
                    Window = new WindowView { From = from, To = composedAt },
                },
                Trail = new TrailView
                {
                    Links = [.. trail.Links
                        .Where(link => !IsDisclosure(link))
                        .Select(ToLink)],
                    UnchainedRows = trail.UnchainedRows,
                    PriorAccesses = [.. trail.Links.Where(IsDisclosure).Select(ToLink)],
                },
                State = new StateView
                {
                    Notification = ToNotification(evidence),
                    Attempts = [.. evidence.Attempts.Select(ToAttempt)],
                    PolicyEvaluations = [.. evidence.PolicyEvaluations.Select(ToEvaluation)],
                    Template = template.IsSuccess ? ToTemplate(template.Value!) : null,
                    Approvals = [.. approvals.Select(ToApproval)],
                    Recipient = recipient,
                },
            };

            // The trail record comes last among the effects and first among the
            // bytes: the answer only becomes a value the endpoint can serialize
            // after the disclosure committed.
            try
            {
                await recorder.RecordAsync(
                    new EvidenceDisclosure
                    {
                        Actor = actor,
                        NotificationId = evidence.Id,
                        Application = evidence.Application,
                        RecipientId = evidence.RecipientId,
                        Attempts = [.. evidence.Attempts.Select(attempt => new DisclosedAttemptHashes(
                            attempt.Sequence, attempt.ContentHashMasked, attempt.ContentHashFull))],
                        TrailLinkCount = response.Trail.Links.Count,
                        PriorAccessCount = response.Trail.PriorAccesses.Count,
                    },
                    cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.DisclosureRecordFailed(exception, actor.ActorId);
                return Result.IntegrationFailure<Response>(
                    "A trilha não registrou a divulgação desta leitura.");
            }

            return Result.Success(response);
        }

        /// <summary>
        /// Every subject this answer reaches: the notification, the recipient
        /// whose contact history it describes, and each device registration a
        /// push attempt targeted, whose invalidation reason lives in the trail
        /// and nowhere else.
        /// </summary>
        private static AuditSubject[] Subjects(NotificationEvidence evidence)
        =>
        [
            new AuditSubject(AuditEntityTypes.Notification, evidence.Id.ToString()),
            new AuditSubject(AuditEntityTypes.Recipient, evidence.RecipientId),
            .. evidence.Attempts
                .Select(attempt => attempt.DeviceTokenId)
                .OfType<Guid>()
                .Distinct()
                .Select(id => new AuditSubject(AuditEntityTypes.DeviceToken, id.ToString())),
        ];

        private async Task<RecipientView> ReadRecipientAsync(
            NotificationEvidence evidence,
            DateTimeOffset from,
            DateTimeOffset to,
            CancellationToken cancellationToken)
        {
            Result<IReadOnlyList<HistoricalContactPoint>> contactPoints =
                await contacts.DescribeContactPointsAsync(
                    evidence.RecipientId,
                    [.. evidence.Attempts.Select(attempt => attempt.ContactPointId).OfType<Guid>().Distinct()],
                    cancellationToken);
            Result<IReadOnlyList<HistoricalDeviceRegistration>> devices =
                await contacts.DescribeDeviceRegistrationsAsync(
                    evidence.RecipientId,
                    [.. evidence.Attempts.Select(attempt => attempt.DeviceTokenId).OfType<Guid>().Distinct()],
                    cancellationToken);
            Result<IReadOnlyList<ConsentLedgerEntry>> ledger = await contacts.ReadConsentLedgerAsync(
                evidence.RecipientId, from, to, cancellationToken);

            return new RecipientView
            {
                RecipientId = evidence.RecipientId,
                ContactPoints = [.. (contactPoints.Value ?? []).Select(ToContactPoint)],
                Devices = [.. (devices.Value ?? []).Select(ToDevice)],
                ConsentLedger = [.. (ledger.Value ?? []).Select(ToConsentEntry)],
            };
        }

        private static bool IsDisclosure(AuditLink link)
            => string.Equals(link.Action, AuditActions.AuditRead, StringComparison.Ordinal);
    }
}
