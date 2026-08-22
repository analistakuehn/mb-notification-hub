using NotificationHub.Api.Modules.TemplateManagement.Domain;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Http;

/// <summary>
/// Translates failed results into RFC 9457 problem responses. The problem
/// <c>type</c> carries the stable error code; state-transition conflicts expose
/// the current status and the allowed transitions as extension members.
/// </summary>
internal static class ApiResults
{
    internal static IResult Problem<T>(Result<T> result)
        => Problem(result.ErrorKind, result.Error);

    /// <summary>
    /// Problem response for an operation blocked by the integral template
    /// validation: 422 with the full report in the <c>checks</c> extension
    /// member. The report is data about the content, not an error string.
    /// </summary>
    internal static IResult Problem(ValidationReport report, string code, string detail)
        => Results.Problem(
            detail: detail,
            statusCode: StatusCodes.Status422UnprocessableEntity,
            title: code,
            type: code,
            extensions: new Dictionary<string, object?>
            {
                ["checks"] = report.Checks,
            });

    internal static IResult Problem(ResultErrorKind kind, string? error)
    {
        DomainErrorInfo info = DomainError.Describe(error, kind);
        var statusCode = StatusCodeFor(kind, info.Code);

        Dictionary<string, object?>? extensions = null;
        if (info.CurrentStatus is not null)
        {
            extensions = new Dictionary<string, object?>
            {
                ["currentStatus"] = info.CurrentStatus,
                ["allowedTransitions"] = info.AllowedTransitions,
            };
        }

        return Results.Problem(
            detail: info.Detail,
            statusCode: statusCode,
            title: info.Code,
            type: info.Code,
            extensions: extensions);
    }

    private static int StatusCodeFor(ResultErrorKind kind, string code) => code switch
    {
        ErrorCodes.PreconditionFailed => StatusCodes.Status412PreconditionFailed,
        _ => kind switch
        {
            ResultErrorKind.Validation => StatusCodes.Status400BadRequest,
            ResultErrorKind.BusinessRule => StatusCodes.Status409Conflict,
            ResultErrorKind.NotFound => StatusCodes.Status404NotFound,
            ResultErrorKind.Forbidden => StatusCodes.Status403Forbidden,
            ResultErrorKind.Integration => StatusCodes.Status502BadGateway,
            _ => StatusCodes.Status500InternalServerError,
        },
    };
}
