using System.Globalization;
using NotificationHub.Api.Modules.Notifications.Infrastructure.RateLimiting;
using NotificationHub.Api.Modules.Notifications.Integration.V1;
using NotificationHub.Api.Modules.TemplateManagement.Integration.V1;

namespace NotificationHub.Api.Modules.Notifications.Infrastructure.Http;

/// <summary>
/// RFC 9457 problem responses of the ingestion surface. The problem
/// <c>type</c> carries the stable rejection code; template rejections expose
/// the failed variable checks as the <c>checks</c> extension member.
///
/// Two codes are protocol conditions of this route and deliberately stay out
/// of the canonical catalog, because neither ever reaches the bus as the
/// <c>reason</c> of a rejection event: <see cref="IdempotencyKeyRequiredType"/>
/// and <see cref="PrincipalRateLimitedType"/>. Every other code answered here
/// is a catalog member.
/// </summary>
internal static class IngestionProblems
{
    internal const string IdempotencyKeyRequiredType = "idempotency-key-required";
    internal const string IdempotencyKeyConflictType = "idempotency-key-conflict";
    internal const string ClassNotAllowedType = "class-not-allowed-for-principal";

    /// <summary>
    /// The producer's own request budget is exhausted. It is a code of its own
    /// because it asks the opposite of the recipient budget: slow down and
    /// retry, instead of stop retrying. It records no trail and announces
    /// nothing on the bus, so it has no place in the catalog.
    /// </summary>
    internal const string PrincipalRateLimitedType = "principal-rate-limited";

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

    /// <summary>
    /// The 429 of the ingestion, named by the dimension that refused. The two
    /// dimensions ask the producer for opposite behaviors, so collapsing them
    /// into one code would leave a client unable to tell "the customer is
    /// protected, stop" from "you are too fast, slow down and retry".
    /// </summary>
    internal static IResult RateLimited(RateLimitedDimension dimension, int retryAfterSeconds)
    {
        var recipient = dimension == RateLimitedDimension.Recipient;
        return new RateLimitedResult(
            retryAfterSeconds,
            Problem(
                StatusCodes.Status429TooManyRequests,
                recipient ? NotificationRejectionReasons.RecipientRateLimited : PrincipalRateLimitedType,
                recipient
                    ? "O orçamento de notificações deste destinatário na classe pedida se esgotou; não retente em laço."
                    : "O limite de solicitações do seu principal foi atingido; reduza a vazão e tente novamente após o intervalo indicado."));
    }

    /// <summary>
    /// The shape refusal of the ingestion, carrying the same per-field
    /// <c>errors</c> dictionary the framework publishes and the catalog code
    /// as the <c>type</c>, so the same defect is named the same way on both
    /// transports.
    /// </summary>
    internal static IResult PayloadInvalid(IReadOnlyDictionary<string, string[]> errors)
        => Results.ValidationProblem(
            new Dictionary<string, string[]>(errors, StringComparer.Ordinal),
            detail: "O corpo da solicitação não passa nas regras de forma da ingestão.",
            statusCode: StatusCodes.Status400BadRequest,
            title: NotificationRejectionReasons.PayloadInvalid,
            type: NotificationRejectionReasons.PayloadInvalid);

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
