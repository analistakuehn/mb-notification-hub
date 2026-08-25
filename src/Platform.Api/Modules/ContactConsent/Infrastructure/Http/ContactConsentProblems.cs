namespace NotificationHub.Api.Modules.ContactConsent.Infrastructure.Http;

/// <summary>
/// RFC 9457 problem responses of the contact and consent write surface. The
/// problem <c>type</c> carries the stable rejection code, in the same dialect
/// as the ingestion problems.
/// </summary>
internal static class ContactConsentProblems
{
    internal const string RecipientIdInvalidType = "recipient-id-invalid";
    internal const string RecipientNotFoundType = "recipient-not-found";
    internal const string NoContactPointForChannelType = "no-contact-point-for-channel";
    internal const string ContactPointNotFoundType = "contact-point-not-found";
    internal const string WriterIdentityRequiredType = "writer-identity-required";
    internal const string ConcurrentUpdateConflictType = "concurrent-update-conflict";

    internal static IResult RecipientIdInvalid()
        => Problem(
            StatusCodes.Status400BadRequest,
            RecipientIdInvalidType,
            "O identificador do destinatário na rota é obrigatório e tem no máximo 100 caracteres.");

    internal static IResult RecipientNotFound(string recipientId)
        => Problem(
            StatusCodes.Status404NotFound,
            RecipientNotFoundType,
            $"O destinatário '{recipientId}' não possui cadastro de contatos.");

    internal static IResult ContactPointNotFound()
        => Problem(
            StatusCodes.Status404NotFound,
            ContactPointNotFoundType,
            "O ponto de contato não existe ou pertence a outro destinatário.");

    internal static IResult NoContactPointForChannel(string channel)
        => Problem(
            StatusCodes.Status422UnprocessableEntity,
            NoContactPointForChannelType,
            $"Não há ponto de contato ativo no canal '{channel}' para ancorar o consentimento declarado.");

    internal static IResult WriterIdentityRequired()
        => Problem(
            StatusCodes.Status403Forbidden,
            WriterIdentityRequiredType,
            "O token do chamador não carrega uma identidade estável (appid, oid ou sub).");

    internal static IResult ConcurrentUpdateConflict()
        => Problem(
            StatusCodes.Status409Conflict,
            ConcurrentUpdateConflictType,
            "Outra declaração para o mesmo destinatário foi gravada ao mesmo tempo; repita a operação.");

    private static IResult Problem(int statusCode, string type, string detail)
        => Results.Problem(
            detail: detail,
            statusCode: statusCode,
            title: type,
            type: type);
}
