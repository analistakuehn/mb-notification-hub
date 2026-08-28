using NotificationHub.Api.Modules.Compliance.Infrastructure.Disclosure;
using NotificationHub.Api.Modules.Compliance.Infrastructure.Http;
using NotificationHub.Api.Modules.Notifications.Integration.V1;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.Compliance.Features.Disclosure;

internal static partial class GetAttemptContent
{
    /// <summary>
    /// Opens the stored content of one attempt through the owning module, checks
    /// the masked hash it recomputed, and records the disclosure before the
    /// answer becomes a value the endpoint can serialize.
    /// </summary>
    internal sealed class Handler(
        INotificationEvidence notifications,
        DisclosureRecorder recorder,
        ContentDisclosureAlarm alarm,
        ILogger<Handler> logger)
    {
        public async Task<Result<Response>> HandleAsync(
            AttemptContentQuery query,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(query);

            Result<NotificationEvidence> found = await notifications.FindAsync(
                query.NotificationId, cancellationToken);
            if (found.IsFailure)
            {
                return new Result<Response>(false, null, found.ErrorKind, found.Error);
            }

            NotificationEvidence evidence = found.Value!;
            Result<RevealedAttemptContent> revealed = await notifications.RevealAttemptContentAsync(
                query.NotificationId, query.Sequence, cancellationToken);
            if (revealed.IsFailure)
            {
                return new Result<Response>(false, null, revealed.ErrorKind, revealed.Error);
            }

            RevealedAttemptContent content = revealed.Value!;
            var verified = string.Equals(
                content.RecomputedContentHashMasked,
                content.ContentHashMasked,
                StringComparison.Ordinal);

            var response = new Response
            {
                NotificationId = NotificationIdentity.Format(evidence.Id),
                Sequence = content.Sequence,
                AttemptStatus = content.AttemptStatus,
                Channel = content.Channel,
                Locale = content.Locale,
                Body = content.Body,
                Subject = content.Subject,
                BodyText = content.BodyText,
                DisclosedForm = DisclosedContentForms.Masked,
                ContentHashMasked = content.ContentHashMasked,
                RecomputedContentHashMasked = content.RecomputedContentHashMasked,
                ContentHashMaskedVerified = verified,
                ContentHashFull = content.ContentHashFull,
                CompleteFormStillStored = content.CompleteFormStillStored,
            };

            try
            {
                await recorder.RecordAsync(
                    new ContentDisclosure
                    {
                        Actor = query.Actor,
                        NotificationId = evidence.Id,
                        Application = evidence.Application,
                        Sequence = content.Sequence,
                        DisclosedForm = response.DisclosedForm,
                        ContentHashMasked = content.ContentHashMasked,
                        ContentHashFull = content.ContentHashFull,
                        ContentHashVerified = verified,
                    },
                    cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.ContentDisclosureRecordFailed(exception, query.Actor.ActorId);
                return Result.IntegrationFailure<Response>(
                    "A trilha não registrou a divulgação desta leitura.");
            }

            // Counted only after the disclosure committed: an opening that was
            // refused disclosed nothing and must not feed the volume alarm.
            alarm.RecordDisclosure(query.Actor.ActorId);
            return Result.Success(response);
        }
    }

    /// <summary>One opening of stored content: which attempt, asked by whom, through which route.</summary>
    internal sealed record AttemptContentQuery(Guid NotificationId, int Sequence, DisclosureActor Actor);
}
