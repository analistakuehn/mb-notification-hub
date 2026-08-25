using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NotificationHub.Api.Infrastructure.Cryptography;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Auditing;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Persistence;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Privacy;
using NotificationHub.Api.Modules.Notifications.Integration.V1;
using NotificationHub.Api.Modules.TemplateManagement.Integration.V1;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.Notifications.Infrastructure.Reads;

/// <summary>
/// The reconstruction reads of this module over the read-only context. The
/// policy evidence is projected here, inside the module that owns the rules, so
/// no consumer ever holds the raw document; the rendered content is opened here,
/// inside the module that owns the key scope, so no consumer ever holds the
/// envelope.
/// </summary>
internal sealed class NotificationEvidenceReader(
    NotificationsReadDbContext db,
    IEnvelopeCipher cipher) : INotificationEvidence
{
    /// <summary>
    /// How far before the notification's own instant the feedback read reaches.
    /// A callback cannot be taken before the send it describes, so the
    /// notification's creation is the true floor and it prunes every earlier
    /// monthly partition of the feedback table. The margin is there because the
    /// two instants are stamped by different processes: a second of clock skew
    /// between them must never drop a row out of an evidence answer, and with
    /// monthly partitions the margin costs one extra partition only for a
    /// notification created right after a month turned.
    /// </summary>
    private static readonly TimeSpan DeliveryFeedbackFloorMargin = TimeSpan.FromHours(1);

    public async Task<Result<NotificationEvidence>> FindAsync(
        Guid notificationId,
        CancellationToken cancellationToken)
    {
        NotificationRow? notification = await db.Notifications
            .Where(candidate => candidate.Id == notificationId)
            .Select(candidate => new NotificationRow(
                candidate.Id,
                candidate.Application,
                candidate.RecipientId,
                candidate.Class,
                candidate.Status,
                candidate.TemplateKey,
                candidate.TemplateVersion,
                candidate.PolicyVersion,
                candidate.CorrelationId,
                candidate.RequestedBy,
                candidate.VariablesMaskedJson,
                candidate.ReleaseAt,
                candidate.ExpiresAt,
                candidate.CreatedAt))
            .FirstOrDefaultAsync(cancellationToken);
        if (notification is null)
        {
            return Result.NotFound<NotificationEvidence>("A notificação solicitada não está disponível.");
        }

        // Explicit projections: the encrypted render and the masked variables
        // are never selected here. Content leaves through the reveal read, one
        // attempt at a time, and every one of those is its own disclosure.
        List<AttemptRow> attempts = await db.NotificationAttempts
            .Where(attempt => attempt.NotificationId == notificationId)
            .OrderBy(attempt => attempt.Sequence)
            .Select(attempt => new AttemptRow(
                attempt.Id,
                attempt.Sequence,
                attempt.Channel,
                attempt.Status,
                attempt.ProviderKey,
                attempt.ProviderMessageId,
                attempt.ContactPointId,
                attempt.DeviceTokenId,
                attempt.ContentHashFull,
                attempt.ContentHashMasked,
                attempt.ErrorCode,
                attempt.FallbackDeadline,
                attempt.SentAt,
                attempt.DeliveredAt,
                attempt.CreatedAt))
            .ToListAsync(cancellationToken);

        ILookup<Guid, DeliveryEventEvidence> feedback = await ReadDeliveryFeedbackAsync(
            notificationId, notification.CreatedAt, cancellationToken);

        List<EvaluationRow> evaluations = await db.PolicyEvaluations
            .Where(evaluation => evaluation.NotificationId == notificationId)
            .OrderBy(evaluation => evaluation.EvaluatedAt)
            .ThenBy(evaluation => evaluation.Id)
            .Select(evaluation => new EvaluationRow(
                evaluation.Rule,
                evaluation.Result,
                evaluation.Reason,
                evaluation.EvidenceJson,
                evaluation.EvaluatedAt))
            .ToListAsync(cancellationToken);

        return Result.Success(new NotificationEvidence
        {
            Id = notification.Id,
            Application = notification.Application,
            RecipientId = notification.RecipientId,
            Class = notification.Class,
            Status = notification.Status,
            TemplateKey = notification.TemplateKey,
            TemplateVersion = notification.TemplateVersion,
            PolicyVersion = notification.PolicyVersion,
            CorrelationId = notification.CorrelationId,
            RequestedBy = notification.RequestedBy,
            ReleaseAt = notification.ReleaseAt,
            ExpiresAt = notification.ExpiresAt,
            VariablesMasked = ParseDocument(notification.VariablesMaskedJson),
            CreatedAt = notification.CreatedAt,
            Attempts = [.. attempts.Select(attempt => ToAttempt(attempt, feedback[attempt.Id]))],
            PolicyEvaluations = [.. evaluations.Select(ToEvaluation)],
        });
    }

    public async Task<Result<RevealedAttemptContent>> RevealAttemptContentAsync(
        Guid notificationId,
        int sequence,
        CancellationToken cancellationToken)
    {
        AttemptContentRow? attempt = await db.NotificationAttempts
            .Where(candidate => candidate.NotificationId == notificationId && candidate.Sequence == sequence)
            .Join(
                db.Notifications.Where(candidate => candidate.Id == notificationId),
                candidate => candidate.NotificationId,
                notification => notification.Id,
                (candidate, notification) => new AttemptContentRow(
                    candidate.Sequence,
                    candidate.Status,
                    notification.Application,
                    candidate.RenderedContentEncrypted,
                    candidate.ContentHashFull,
                    candidate.ContentHashMasked))
            .FirstOrDefaultAsync(cancellationToken);
        if (attempt is null)
        {
            return Result.NotFound<RevealedAttemptContent>(
                "A tentativa solicitada não está disponível para a notificação informada.");
        }

        SealedRenderedContent content = await RenderedContentEnvelope.ReadMaskedAsync(
            cipher, attempt.Application, attempt.RenderedContentEncrypted, cancellationToken);

        return Result.Success(new RevealedAttemptContent
        {
            Sequence = attempt.Sequence,
            AttemptStatus = attempt.Status,
            Channel = content.Channel,
            Locale = content.Locale,
            Subject = content.Subject,
            Body = content.Body,
            BodyText = content.BodyText,
            ContentHashMasked = attempt.ContentHashMasked,
            ContentHashFull = attempt.ContentHashFull,

            // The catalog owns the canonical rule; recomputing it here would be
            // a second implementation, and two implementations of one hash turn
            // a verification failure into an argument about whose hash is right.
            RecomputedContentHashMasked = RenderedContentHash.OfForm(
                content.Subject, content.Body, content.BodyText),
            CompleteFormStillStored = content.CarriesMaskedForm,
        });
    }

    /// <summary>
    /// Reads the provider feedback of one notification, grouped by the attempt
    /// each piece describes and ordered by the instant the provider says it
    /// happened. The sealed provider payload is never selected: it carries the
    /// destination in the clear, and a column left out of the projection cannot
    /// be served by accident later.
    /// </summary>
    private async Task<ILookup<Guid, DeliveryEventEvidence>> ReadDeliveryFeedbackAsync(
        Guid notificationId,
        DateTimeOffset notificationCreatedAt,
        CancellationToken cancellationToken)
    {
        DateTimeOffset receivedFrom = notificationCreatedAt - DeliveryFeedbackFloorMargin;
        List<DeliveryEventRow> rows = await db.DeliveryEvents

            // The correlation columns are written together, so feedback that
            // names the notification also names the attempt; a row that named
            // only the notification would have no attempt to sit under.
            .Where(feedback => feedback.NotificationId == notificationId
                && feedback.AttemptId != null
                && feedback.ReceivedAt >= receivedFrom)

            // The provider's own chronology, because that is the only instant
            // this projection publishes. Reception and identity break ties, so
            // two events stamped alike still come back in one stable order.
            .OrderBy(feedback => feedback.OccurredAt)
            .ThenBy(feedback => feedback.ReceivedAt)
            .ThenBy(feedback => feedback.Id)
            .Select(feedback => new DeliveryEventRow(
                feedback.AttemptId,
                feedback.ProviderKey,
                feedback.ProviderEventId,
                feedback.Kind,
                feedback.OccurredAt,
                feedback.ErrorCode))
            .ToListAsync(cancellationToken);

        return rows.ToLookup(
            row => row.AttemptId!.Value,
            row => new DeliveryEventEvidence
            {
                ProviderKey = row.ProviderKey,
                ProviderEventId = row.ProviderEventId,
                Kind = row.Kind,
                OccurredAt = row.OccurredAt,
                ErrorCode = row.ErrorCode,
            });
    }

    private static NotificationAttemptEvidence ToAttempt(
        AttemptRow row,
        IEnumerable<DeliveryEventEvidence> feedback)
        => new()
        {
            Sequence = row.Sequence,
            Channel = row.Channel,
            Status = row.Status,
            ProviderKey = row.ProviderKey,
            ProviderMessageId = row.ProviderMessageId,
            ContactPointId = row.ContactPointId,
            DeviceTokenId = row.DeviceTokenId,
            ContentHashFull = row.ContentHashFull,
            ContentHashMasked = row.ContentHashMasked,
            ErrorCode = row.ErrorCode,
            FallbackDeadline = row.FallbackDeadline,
            SentAt = row.SentAt,
            DeliveredAt = row.DeliveredAt,
            DeliveryEvents = [.. feedback],
            CreatedAt = row.CreatedAt,
        };

    /// <summary>
    /// Reads one stored JSON document into a detached element. The column is
    /// written by this module and is never null, so a document that does not
    /// parse is corruption of the row rather than a shape to smooth over.
    /// </summary>
    private static JsonElement ParseDocument(string json)
    {
        using var parsed = JsonDocument.Parse(json);
        return parsed.RootElement.Clone();
    }

    private static PolicyEvaluationEvidence ToEvaluation(EvaluationRow row)
    {
        PolicyEvidenceView view = PolicyEvidenceProjection.Project(row.Rule, row.EvidenceJson);
        return new PolicyEvaluationEvidence
        {
            Rule = row.Rule,
            Result = row.Result,
            Reason = row.Reason,
            EvaluatedAt = row.EvaluatedAt,
            Evidence = view.Evidence,
            UndisclosedEvidenceKeys = view.UndisclosedKeys,
        };
    }

    private sealed record NotificationRow(
        Guid Id,
        string Application,
        string RecipientId,
        string Class,
        string Status,
        string TemplateKey,
        int TemplateVersion,
        int? PolicyVersion,
        string? CorrelationId,
        string RequestedBy,
        string VariablesMaskedJson,
        DateTimeOffset? ReleaseAt,
        DateTimeOffset ExpiresAt,
        DateTimeOffset CreatedAt);

    private sealed record AttemptRow(
        Guid Id,
        int Sequence,
        string Channel,
        string Status,
        string? ProviderKey,
        string? ProviderMessageId,
        Guid? ContactPointId,
        Guid? DeviceTokenId,
        string ContentHashFull,
        string ContentHashMasked,
        string? ErrorCode,
        DateTimeOffset? FallbackDeadline,
        DateTimeOffset? SentAt,
        DateTimeOffset? DeliveredAt,
        DateTimeOffset CreatedAt);

    private sealed record DeliveryEventRow(
        Guid? AttemptId,
        string ProviderKey,
        string ProviderEventId,
        string Kind,
        DateTimeOffset OccurredAt,
        string? ErrorCode);

    private sealed record EvaluationRow(
        string Rule,
        string Result,
        string? Reason,
        string EvidenceJson,
        DateTimeOffset EvaluatedAt);

    private sealed record AttemptContentRow(
        int Sequence,
        string Status,
        string Application,
        byte[] RenderedContentEncrypted,
        string ContentHashFull,
        string ContentHashMasked);
}
