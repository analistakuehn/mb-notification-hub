using Microsoft.EntityFrameworkCore;
using NotificationHub.Api.Modules.ContactConsent.Integration.V1;
using NotificationHub.Api.Modules.Notifications.Domain;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Persistence;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Reads;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.Notifications.Features.Queries;

internal static partial class GetNotification
{
    internal sealed class Handler(
        NotificationsReadDbContext db,
        AttemptTargetDirectory targets)
    {
        public async Task<Result<Response>> HandleAsync(Guid notificationId, CancellationToken cancellationToken)
        {
            // Explicit projections on purpose: the encrypted render and the
            // masked variables are never selected, so no later refactor of the
            // response can reach a column the contract forbids.
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
                    candidate.ReleaseAt,
                    candidate.ExpiresAt,
                    candidate.CreatedAt))
                .FirstOrDefaultAsync(cancellationToken);
            if (notification is null)
            {
                return Result.NotFound<Response>("A notificação solicitada não está disponível.");
            }

            List<AttemptRow> attempts = await db.NotificationAttempts
                .Where(attempt => attempt.NotificationId == notificationId)
                .OrderBy(attempt => attempt.Sequence)
                .Select(attempt => new AttemptRow(
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

            List<Evaluation> evaluations = await db.PolicyEvaluations
                .Where(evaluation => evaluation.NotificationId == notificationId)
                .OrderBy(evaluation => evaluation.EvaluatedAt)
                .ThenBy(evaluation => evaluation.Id)
                .Select(evaluation => new Evaluation
                {
                    Rule = evaluation.Rule,
                    Result = evaluation.Result,
                    Reason = evaluation.Reason,
                    EvaluatedAt = evaluation.EvaluatedAt,
                })
                .ToListAsync(cancellationToken);

            AttemptTargets resolved = await targets.ResolveAsync(
                notification.RecipientId,
                [.. attempts.Select(attempt => attempt.ContactPointId).OfType<Guid>().Distinct()],
                [.. attempts.Select(attempt => attempt.DeviceTokenId).OfType<Guid>().Distinct()],
                cancellationToken);

            return Result.Success(new Response
            {
                Id = NotificationId.Format(notification.Id),
                Application = notification.Application,
                Class = notification.Class,
                Status = notification.Status,
                TemplateKey = notification.TemplateKey,
                TemplateVersion = notification.TemplateVersion,
                RequestedBy = notification.RequestedBy,
                CreatedAt = notification.CreatedAt,
                ExpiresAt = notification.ExpiresAt,
                CorrelationId = notification.CorrelationId,
                PolicyVersion = notification.PolicyVersion,
                ReleaseAt = notification.ReleaseAt,
                PolicyEvaluations = evaluations,
                Attempts = [.. attempts.Select(attempt => ToAttempt(attempt, resolved))],
            });
        }

        private static Attempt ToAttempt(AttemptRow row, AttemptTargets resolved) => new()
        {
            Sequence = row.Sequence,
            Channel = row.Channel,
            Status = row.Status,
            ContentHashFull = row.ContentHashFull,
            ContentHashMasked = row.ContentHashMasked,
            CreatedAt = row.CreatedAt,
            ProviderKey = row.ProviderKey,
            ProviderMessageId = row.ProviderMessageId,
            ErrorCode = row.ErrorCode,
            FallbackDeadline = row.FallbackDeadline,
            SentAt = row.SentAt,
            DeliveredAt = row.DeliveredAt,
            Target = ToTarget(row, resolved),
        };

        private static Target? ToTarget(AttemptRow row, AttemptTargets resolved)
        {
            if (row.ContactPointId is { } contactPointId)
            {
                resolved.ContactPoints.TryGetValue(contactPointId, out MaskedContactPoint? masked);
                return new Target
                {
                    Kind = Target.ContactPointKind,
                    ContactPointId = contactPointId,
                    Masked = masked?.MaskedValue,
                    Active = masked?.Active,
                };
            }

            if (row.DeviceTokenId is { } deviceTokenId)
            {
                // An answered directory read decides the flag: the identity is
                // either among the active registrations or it is not. A read
                // that never answered leaves both members out, because absence
                // is how this response says the phase does not know.
                var active = resolved.DevicePlatforms.TryGetValue(deviceTokenId, out var platform);
                return new Target
                {
                    Kind = Target.DeviceKind,
                    DeviceTokenId = deviceTokenId,
                    Platform = active ? platform : null,
                    Active = resolved.DeviceRegistrationsAnswered ? active : null,
                };
            }

            return null;
        }
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
        DateTimeOffset? ReleaseAt,
        DateTimeOffset ExpiresAt,
        DateTimeOffset CreatedAt);

    private sealed record AttemptRow(
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
}
