namespace NotificationHub.Api.Modules.Compliance.Infrastructure.Http;

/// <summary>
/// RFC 9457 problem responses of the audit surface. The not-found body is
/// identical for every unknown identity and never echoes the value the caller
/// sent: an answer that varied with the input would turn the route into an
/// existence oracle even behind the audit role.
/// </summary>
internal static class AuditProblems
{
    internal const string InvalidRequestType = "invalid-request";
    internal const string NotFoundType = "audit-subject-not-found";
    internal const string DisclosureUnavailableType = "disclosure-record-unavailable";

    internal static IResult InvalidRequest(string detail)
        => Problem(StatusCodes.Status400BadRequest, InvalidRequestType, detail);

    internal static IResult NotFound()
        => Problem(
            StatusCodes.Status404NotFound,
            NotFoundType,
            "O sujeito solicitado não está disponível para reconstrução.");

    /// <summary>
    /// The disclosure could not be recorded, so nothing is disclosed. It is a
    /// refusal to answer, not a failure of the evidence: retrying is legitimate
    /// and the answer will be identical once the trail accepts the record.
    /// </summary>
    internal static IResult DisclosureUnavailable()
        => Problem(
            StatusCodes.Status503ServiceUnavailable,
            DisclosureUnavailableType,
            "A trilha não registrou a divulgação desta leitura, então nada foi divulgado. Tente novamente.");

    private static IResult Problem(int statusCode, string type, string detail)
        => Results.Problem(detail: detail, statusCode: statusCode, title: type, type: type);
}
