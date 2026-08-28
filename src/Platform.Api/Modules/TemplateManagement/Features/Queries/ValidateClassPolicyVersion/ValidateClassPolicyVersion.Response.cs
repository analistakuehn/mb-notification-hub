using NotificationHub.Api.Modules.TemplateManagement.Domain;

namespace NotificationHub.Api.Modules.TemplateManagement.Features.Queries;

internal static partial class ValidateClassPolicyVersion
{
    internal sealed record CheckEntry(string Name, string Status, string Message, string? Location);

    internal sealed record Response(bool Passed, IReadOnlyList<CheckEntry> Checks)
    {
        internal static Response From(ValidationReport report) => new(
            report.Passed,
            report.Checks
                .Select(check => new CheckEntry(check.Name, check.Status, check.Message, check.Location))
                .ToList());
    }
}
