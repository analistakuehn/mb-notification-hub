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
/// Four HTTP-only codes deliberately stay out of the canonical catalog:
/// <see cref="IdempotencyKeyRequiredType"/>,
/// <see cref="PrincipalRateLimitedType"/>,
/// <see cref="KillSwitchUnavailableType"/>, and
/// <see cref="AttachmentsNotClaimableType"/>. None reaches the bus as the
/// <c>reason</c> of a rejection event. Every other code answered here is a
/// catalog member.
/// </summary>
internal static class IngestionProblems
{
    internal const string IdempotencyKeyRequiredType = "idempotency-key-required";
    internal const string IdempotencyKeyConflictType = "idempotency-key-conflict";
    internal const string ClassNotAllowedType = "class-not-allowed-for-principal";
    internal const string KillSwitchUnavailableType = "kill-switch-unavailable";

    /// <summary>
    /// The request names attachments that cannot be claimed for it. It stays
    /// out of the canonical catalog and off the bus for now: the catalog is
    /// the vocabulary a producer looks up in the published integration guide,
    /// and a member that exists in code and nowhere in that guide reaches a
    /// producer as a word it cannot look up. The guide and the catalog entry
    /// are published together, by the task that owns that document.
    /// </summary>
    internal const string AttachmentsNotClaimableType = "attachments-not-claimable";

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

    internal static IResult ProducerDisabled()
        => Problem(
            StatusCodes.Status403Forbidden,
            NotificationRejectionReasons.ProducerDisabled,
            "O produtor está bloqueado pelo controle de emergência.");

    internal static IResult KillSwitchUnavailable()
        => Problem(
            StatusCodes.Status503ServiceUnavailable,
            KillSwitchUnavailableType,
            "A autoridade do controle de emergência está indisponível.");

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
    /// The 422 of a set that was not claimed. It names no member and no
    /// reason: which attachment refused, and whether it was never released,
    /// released to someone else or released and taken back, is a reading of
    /// the lifecycle that the operations surface answers to a caller that may
    /// ask it.
    /// </summary>
    internal static IResult AttachmentsNotClaimable()
        => Problem(
            StatusCodes.Status422UnprocessableEntity,
            AttachmentsNotClaimableType,
            "Os anexos informados não podem ser vinculados a esta solicitação.");

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
