using NotificationHub.Api.Modules.TemplateManagement.Domain;

namespace NotificationHub.Api.Modules.TemplateManagement.Integration.V1;

/// <summary>Canonical outcome values a validation check can report.</summary>
public static class VariablesValidationStatuses
{
    public const string Passed = ValidationCheckStatuses.Passed;
    public const string Warning = ValidationCheckStatuses.Warning;
    public const string Failed = ValidationCheckStatuses.Failed;
}

/// <summary>
/// One verification of the variables validation, in the shared checks
/// vocabulary: a stable check name, an outcome from
/// <see cref="VariablesValidationStatuses"/>, a message that names the
/// variable but never carries its value, and an optional location.
/// </summary>
public sealed record VariablesValidationCheck(string Name, string Status, string Message, string? Location);

/// <summary>
/// Full report of validating a variables payload against the variables schema
/// of the published version. The report is a value, never an error: running
/// the validation succeeds even when checks fail, and a failed check is data
/// for the consumer's own rejection decision.
/// </summary>
/// <remarks>
/// This is a reference type without value equality on purpose. A report is the
/// outcome of one run, and comparing two of them would compare the check-list
/// instance the validator happened to build, which is a fact about that
/// validator and not about the payload it read. A consumer that wants to know
/// whether two payloads validate alike compares the checks it reads, never the
/// reports it holds.
/// </remarks>
public sealed class VariablesValidationReport(IReadOnlyList<VariablesValidationCheck> checks)
{
    /// <summary>The verifications of this run, in the order the validation produced them.</summary>
    public IReadOnlyList<VariablesValidationCheck> Checks { get; } = checks;

    public bool Passed => Checks.All(check => check.Status != VariablesValidationStatuses.Failed);
}
