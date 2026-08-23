namespace NotificationHub.Api.Modules.Notifications.Infrastructure.Http;

/// <summary>
/// RFC 9457 problem responses of the query surface. The not-found body is
/// deliberately identical for every unknown identity and never echoes the
/// value the caller sent: an answer that varied with the input would turn the
/// route into an existence oracle.
/// </summary>
internal static class QueryProblems
{
    internal const string InvalidRequestType = "invalid-request";
    internal const string InvalidCursorType = "invalid-cursor";
    internal const string NotFoundType = "notification-not-found";

    internal static IResult InvalidRequest(string detail)
        => Problem(StatusCodes.Status400BadRequest, InvalidRequestType, detail);

    internal static IResult InvalidCursor(string detail)
        => Problem(StatusCodes.Status400BadRequest, InvalidCursorType, detail);

    internal static IResult NotFound()
        => Problem(
            StatusCodes.Status404NotFound,
            NotFoundType,
            "A notificação solicitada não está disponível. "
            + NotificationQueryContract.ReplicationLagNotice);

    private static IResult Problem(int statusCode, string type, string detail)
        => Results.Problem(detail: detail, statusCode: statusCode, title: type, type: type);
}
