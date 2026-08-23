using System.Globalization;
using NotificationHub.Api.Modules.TemplateManagement.Integration.V1;

namespace NotificationHub.Api.Modules.Notifications.Infrastructure.Http;

/// <summary>
/// RFC 9457 problem responses of the ingestion surface. The problem
/// <c>type</c> carries the stable rejection code; template rejections expose
/// the failed variable checks as the <c>checks</c> extension member.
/// </summary>
internal static class IngestionProblems
{
    internal const string IdempotencyKeyRequiredType = "idempotency-key-required";
    internal const string IdempotencyKeyConflictType = "idempotency-key-conflict";
    internal const string ClassNotAllowedType = "class-not-allowed-for-principal";
    internal const string RateLimitExceededType = "rate-limit-exceeded";

    internal static IResult MissingIdempotencyKey()
        => Problem(
            StatusCodes.Status400BadRequest,
            IdempotencyKeyRequiredType,
            "A requisição precisa do cabeçalho Idempotency-Key.");

    internal static IResult ClassNotAllowed(string canonicalClass)
        => Problem(
            StatusCodes.Status403Forbidden,
            ClassNotAllowedType,
            $"O token do produtor não cobre a classe '{canonicalClass}'.");

    internal static IResult IdempotencyConflict()
        => Problem(
            StatusCodes.Status409Conflict,
            IdempotencyKeyConflictType,
            "A mesma chave de idempotência já foi usada com um corpo diferente.");

    internal static IResult TemplateRejection(
        string reason,
        string detail,
        IReadOnlyList<VariablesValidationCheck>? checks)
        => Problem(
            StatusCodes.Status422UnprocessableEntity,
            reason,
            detail,
            checks is null ? null : new Dictionary<string, object?> { ["checks"] = checks });

    internal static IResult RateLimited(int retryAfterSeconds)
        => new RateLimitedResult(
            retryAfterSeconds,
            Problem(
                StatusCodes.Status429TooManyRequests,
                RateLimitExceededType,
                "O limite de solicitações foi atingido; tente novamente após o intervalo indicado."));

    private static IResult Problem(
        int statusCode,
        string type,
        string detail,
        Dictionary<string, object?>? extensions = null)
        => Results.Problem(
            detail: detail,
            statusCode: statusCode,
            title: type,
            type: type,
            extensions: extensions);

    /// <summary>Problem response that also announces when the caller may retry.</summary>
    private sealed class RateLimitedResult(int retryAfterSeconds, IResult problem) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.Headers.RetryAfter =
                retryAfterSeconds.ToString(CultureInfo.InvariantCulture);
            return problem.ExecuteAsync(httpContext);
        }
    }
}
