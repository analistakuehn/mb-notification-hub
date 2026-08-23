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
        List<NotificationAttemptEvidence> attempts = await db.NotificationAttempts
            .Where(attempt => attempt.NotificationId == notificationId)
            .OrderBy(attempt => attempt.Sequence)
            .Select(attempt => new NotificationAttemptEvidence
            {
                Sequence = attempt.Sequence,
                Channel = attempt.Channel,
                Status = attempt.Status,
                ProviderKey = attempt.ProviderKey,
                ProviderMessageId = attempt.ProviderMessageId,
                ContactPointId = attempt.ContactPointId,
                DeviceTokenId = attempt.DeviceTokenId,
                ContentHashFull = attempt.ContentHashFull,
                ContentHashMasked = attempt.ContentHashMasked,
                ErrorCode = attempt.ErrorCode,
                FallbackDeadline = attempt.FallbackDeadline,
                SentAt = attempt.SentAt,
                CreatedAt = attempt.CreatedAt,
            })
            .ToListAsync(cancellationToken);

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
            Attempts = attempts,
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
    /// Reads one stored JSON document into a detached element. The column is
    /// written by this module and is never null, so a document that does not
    /// parse is corruption of the row rather than a shape to smooth over.
    /// </summary>
    private static JsonElement ParseDocument(string json)
    {
        using JsonDocument parsed = JsonDocument.Parse(json);
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
