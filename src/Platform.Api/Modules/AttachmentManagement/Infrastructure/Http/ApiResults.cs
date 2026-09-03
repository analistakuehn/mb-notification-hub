using NotificationHub.Api.Modules.AttachmentManagement.Domain;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Http;

/// <summary>Translates module failures into RFC 9457 problem responses.</summary>
internal static class ApiResults
{
    private const string UnexpectedError = "attachment-operation-failed";

    private static readonly Dictionary<string, string> Details =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ErrorCodes.InvalidMetadata] = "Os metadados do anexo são inválidos.",
            [ErrorCodes.AccessDenied] = "O acesso à aplicação do anexo foi negado.",
            [ErrorCodes.AuthorizationUnavailable] =
                "A autorização de anexos está indisponível.",
            [ErrorCodes.InvalidReference] = "A referência do anexo é inválida.",
            [ErrorCodes.NotFound] = "Anexo não encontrado.",
            [ErrorCodes.SizeMismatch] =
                "O conteúdo enviado não tem o tamanho registrado para o anexo.",
            [ErrorCodes.AlreadyReceived] = "O anexo já foi recebido.",
            [ErrorCodes.UploadConflict] = "Não foi possível concluir o upload do anexo.",
            [ErrorCodes.StoreUnavailable] = "O armazenamento de anexos está indisponível.",
            [ErrorCodes.StoreUnidentifiedGeneration] =
                "O armazenamento aceitou o conteúdo sem identificar a geração gravada.",
            [ErrorCodes.GenerationUnreadable] =
                "A geração recém-gravada do anexo não pôde ser lida de volta.",

            // One line for the whole family of content refusals. Which check
            // refused is durable state and leaves by the authorized query, not
            // by this answer: a producer who could read it here would read a
            // map of what to work around.
            [ErrorCodes.ContentRefused] = "O conteúdo do anexo não foi liberado.",

            [ErrorCodes.ContentMissing] =
                "O conteúdo do anexo ainda não foi recebido, portanto não há o que validar.",
            [ErrorCodes.NotReleased] = "O anexo não tem liberação vigente para revogar.",
            [ErrorCodes.Revoked] = "A liberação do anexo foi revogada.",

            // One line for the whole family of transitions that could not be
            // carried through. Which of them it was stays on the record, for
            // the same reason the refusal of content keeps its detail there.
            [ErrorCodes.LifecycleUnavailable] =
                "O ciclo de vida do anexo não pôde avançar; nada foi gravado.",
        };

    internal static IResult Problem<T>(Result<T> result)
    {
        var code = result.Error;
        if (code is null || !Details.TryGetValue(code, out var detail))
        {
            return Results.Problem(
                detail: "A operação sobre o anexo falhou.",
                statusCode: StatusCodes.Status500InternalServerError,
                title: UnexpectedError,
                type: UnexpectedError);
        }

        return Results.Problem(
            detail: detail,
            statusCode: StatusCodeFor(result.ErrorKind, code),
            title: code,
            type: code);
    }

    private static int StatusCodeFor(ResultErrorKind kind, string code)
        => code switch
        {
            ErrorCodes.StoreUnavailable
                or ErrorCodes.StoreUnidentifiedGeneration
                or ErrorCodes.GenerationUnreadable
                or ErrorCodes.AuthorizationUnavailable
                or ErrorCodes.LifecycleUnavailable =>
                StatusCodes.Status503ServiceUnavailable,
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
