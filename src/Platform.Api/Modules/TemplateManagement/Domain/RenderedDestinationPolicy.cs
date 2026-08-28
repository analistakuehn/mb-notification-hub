using NotificationHub.Api.Modules.TemplateManagement.Integration.V1;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.TemplateManagement.Domain;

/// <summary>
/// Applies the link-domain decision to the completed form a provider would
/// receive. Source and payload checks run earlier as defenses in depth; this
/// check owns the point where fragments, wrappers, and normalization have
/// already combined into one destination.
/// </summary>
internal static class RenderedDestinationPolicy
{
    internal static Result Validate(
        Template template,
        Channel channel,
        string? subject,
        string body,
        string? bodyText)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(body);

        Result subjectResult = ValidateField(template, TemplateContentFields.Subject, subject, inMarkup: false);
        if (subjectResult.IsFailure)
        {
            return subjectResult;
        }

        Result bodyResult = ValidateField(
            template,
            TemplateContentFields.Body,
            body,
            inMarkup: channel == Channel.Email);
        if (bodyResult.IsFailure)
        {
            return bodyResult;
        }

        return ValidateField(template, TemplateContentFields.BodyText, bodyText, inMarkup: false);
    }

    private static Result ValidateField(Template template, string field, string? value, bool inMarkup)
    {
        var offending = inMarkup
            ? LinkDomainPolicy.FirstDisallowedHostInMarkup(value, template)
            : LinkDomainPolicy.FirstDisallowedHost(value, template);
        if (offending is null)
        {
            return Result.Success();
        }

        // Only the host leaves the policy. A destination may carry a token or
        // personal data in its path and query, neither of which belongs in an
        // error response or a log derived from it.
        return new Result(false, ResultErrorKind.Validation, DomainError.Format(
            ErrorCodes.UrlDomainNotAllowed,
            $"Rendered field '{field}' carries link host '{offending}', "
            + "which is outside the template's allowed domains."));
    }
}
