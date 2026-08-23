using System.Text.RegularExpressions;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.TemplateManagement.Domain;

/// <summary>
/// Canonical form of an application identifier: lowercase alphanumeric segments
/// separated by hyphens. Templates carry it as governed metadata and class
/// policies key on it together with the notification class.
/// </summary>
public static partial class ApplicationName
{
    public const int MaxLength = 100;

    public static Result<string> Create(string? value)
    {
        var candidate = value?.Trim() ?? string.Empty;
        return candidate.Length == 0
            || candidate.Length > MaxLength
            || !Pattern().IsMatch(candidate)
            ? Result.ValidationError<string>(DomainError.Format(
                ErrorCodes.InvalidRequest,
                $"Application must be 1-{MaxLength} lowercase alphanumeric characters "
                + "in segments separated by '-'."))
            : Result.Success(candidate);
    }

    [GeneratedRegex(@"^[a-z0-9]+(?:-[a-z0-9]+)*$")]
    private static partial Regex Pattern();
}
