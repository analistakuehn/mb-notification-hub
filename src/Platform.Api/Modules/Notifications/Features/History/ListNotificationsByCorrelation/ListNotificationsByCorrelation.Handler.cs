using NotificationHub.Api.Modules.Notifications.Infrastructure.Reads;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.Notifications.Features.History;

internal static partial class ListNotificationsByCorrelation
{
    internal sealed class Handler(NotificationHistoryReader reader)
    {
        public async Task<Result<NotificationHistoryOutcome>> HandleAsync(
            Query query,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(query);

            // Without a subject there is nothing to answer: a listing of the
            // whole store, or of one application, is exactly the sweep this
            // surface refuses to offer.
            if (string.IsNullOrWhiteSpace(query.CorrelationId))
            {
                return Result.Success<NotificationHistoryOutcome>(
                    new NotificationHistoryOutcome.InvalidRequest(
                        "O parâmetro correlationId é obrigatório nesta rota."));
            }

            NotificationHistoryOutcome outcome = await reader.ReadAsync(
                new NotificationHistoryRequest
                {
                    CorrelationId = query.CorrelationId,
                    Class = query.Class,
                    From = query.From,
                    To = query.To,
                    Cursor = query.Cursor,
                    Limit = query.Limit,
                },
                cancellationToken);

            return Result.Success(outcome);
        }
    }
}
