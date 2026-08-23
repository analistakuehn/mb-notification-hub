using NotificationHub.Api.Modules.Notifications.Infrastructure.Reads;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.Notifications.Infrastructure.Http;

/// <summary>
/// Maps a history outcome to the protocol. Both list routes share it because
/// they share the outcome: the subject differs, the refusals do not.
/// </summary>
internal static class NotificationHistoryResults
{
    internal static IResult From(Result<NotificationHistoryOutcome> result)
    {
        if (result.IsFailure)
        {
            return Results.Problem(statusCode: StatusCodes.Status500InternalServerError);
        }

        return result.Value switch
        {
            NotificationHistoryOutcome.Page page => Results.Ok(page.Value),
            NotificationHistoryOutcome.InvalidCursor invalid => QueryProblems.InvalidCursor(invalid.Detail),
            NotificationHistoryOutcome.InvalidRequest invalid => QueryProblems.InvalidRequest(invalid.Detail),
            _ => Results.Problem(statusCode: StatusCodes.Status500InternalServerError),
        };
    }
}
