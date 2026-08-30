using System.Text.Json;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.TemplateManagement.Domain;

/// <summary>
/// Applies the link-domain decision to the variables payload, before a single
/// field renders. It is the cheap half of the same rule the rendered form gets
/// afterwards: a value the allowlist refuses is refused while it is still one
/// named variable, which is what lets the refusal say which variable is wrong
/// without ever quoting what it carried.
/// <para>
/// Both render paths read this, and they read the same copy on purpose. Two
/// implementations of one allowlist leave it enforced on one side only, with
/// nothing to signal which side went quiet.
/// </para>
/// </summary>
internal static class VariablesDestinationPolicy
{
    internal static Result Validate(Template template, TemplateVersion version, JsonElement? variables)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(version);

        if (!VariablesSchema.TryParse(version.VariablesSchemaJson, out IReadOnlyList<VariableDeclaration> declarations))
        {
            return new Result(false, ResultErrorKind.Validation, DomainError.Format(
                ErrorCodes.TemplateRenderFailed,
                "The variables schema is not readable; run the validation report."));
        }

        if (variables is not { ValueKind: JsonValueKind.Object } provided)
        {
            return Result.Success();
        }

        foreach (VariableDeclaration declaration in declarations.Where(declaration => declaration.IsUrl))
        {
            if (!provided.TryGetProperty(declaration.Name, out JsonElement value))
            {
                continue;
            }

            if (!LinkDomainPolicy.IsAllowedUrlValue(template, value))
            {
                // The value never travels in the error: it may embed tokens
                // or personal data in the query string.
                return new Result(false, ResultErrorKind.Validation, DomainError.Format(
                    ErrorCodes.UrlDomainNotAllowed,
                    $"Variable '{declaration.Name}' must be an absolute http(s) URL "
                    + "inside the template's allowed domains."));
            }
        }

        var offending = LinkDomainPolicy.FirstDisallowedHost(variables, template);
        if (offending is not null)
        {
            // Only the host travels in the error, never the value: the
            // query string may carry a token or personal data. Same reason
            // as the loop above.
            return new Result(false, ResultErrorKind.Validation, DomainError.Format(
                ErrorCodes.UrlDomainNotAllowed,
                $"A variable value carries link host '{offending}', "
                + "which is outside the template's allowed domains."));
        }

        return Result.Success();
    }
}
