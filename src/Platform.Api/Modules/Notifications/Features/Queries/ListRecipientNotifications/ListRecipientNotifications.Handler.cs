using NotificationHub.Api.Modules.Notifications.Infrastructure.Reads;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.Notifications.Features.Queries;

internal static partial class ListRecipientNotifications
{
    internal sealed class Handler(NotificationHistoryReader reader)
    {
        public async Task<Result<NotificationHistoryOutcome>> HandleAsync(
            Query query,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(query);

            NotificationHistoryOutcome outcome = await reader.ReadAsync(
                new NotificationHistoryRequest
                {
                    RecipientId = query.RecipientId,
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
