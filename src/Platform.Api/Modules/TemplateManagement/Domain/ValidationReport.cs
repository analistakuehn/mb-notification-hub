namespace NotificationHub.Api.Modules.TemplateManagement.Domain;

/// <summary>Canonical outcome values a validation check can report.</summary>
public static class ValidationCheckStatuses
{
    public const string Passed = "passed";
    public const string Warning = "warning";
    public const string Failed = "failed";
}

/// <summary>
/// One verification executed by the integral template validation. The location
/// points at the content unit that produced the finding (for example
/// <c>email/pt-BR/body</c>) and is null for template-level checks.
/// </summary>
public sealed record ValidationCheck(string Name, string Status, string Message, string? Location);

/// <summary>
/// Full report of the integral template validation. The report is a response
/// value, never an error: running the validation succeeds even when checks
/// fail, and a failed check only blocks publication.
/// </summary>
public sealed record ValidationReport(IReadOnlyList<ValidationCheck> Checks)
{
    public bool Passed => Checks.All(check => check.Status != ValidationCheckStatuses.Failed);
}
