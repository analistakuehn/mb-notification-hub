using NotificationHub.Api.Modules.TemplateManagement.Domain;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Http;

/// <summary>
/// HTTP entity-tag handling for draft editing. Drafts require <c>If-Match</c>
/// with the current version tag; an absent or stale tag fails the precondition.
/// </summary>
internal static class EntityTags
{
    internal static string ToHeaderValue(string entityTag) => $"\"{entityTag}\"";

    internal static Result CheckIfMatch(string? ifMatch, string currentEntityTag)
    {
        if (string.IsNullOrWhiteSpace(ifMatch))
        {
            return PreconditionFailure(
                "The If-Match header is required to edit a draft. "
                + "Fetch the version to obtain its current entity tag.");
        }

        var normalized = Normalize(ifMatch);
        if (normalized == "*" || string.Equals(normalized, currentEntityTag, StringComparison.Ordinal))
        {
            return Result.Success();
        }

        return PreconditionFailure(
            "The supplied If-Match entity tag no longer matches the version. "
            + "Fetch the version again and retry with its current entity tag.");
    }

    private static string Normalize(string ifMatch)
    {
        var value = ifMatch.Trim();
        if (value.StartsWith("W/", StringComparison.Ordinal))
        {
            value = value[2..];
        }

        return value.Trim('"');
    }

    private static Result PreconditionFailure(string detail)
        => Result.BusinessRuleViolation(DomainError.Format(ErrorCodes.PreconditionFailed, detail));
}
