using NotificationHub.Api.Modules.TemplateManagement.Domain;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.TemplateManagement.Integration.V1;

/// <summary>
/// One ordered step of the delivery plan: which channel to try and how long to
/// wait before falling back to the next step. Steps are objects on the wire so
/// future optional properties extend them without a data migration.
/// </summary>
public sealed record DeliveryPlanStep(Channel Channel, TimeSpan? Timeout);

/// <summary>Daily silence window applied in the recipient's timezone.</summary>
public sealed record QuietHoursWindow(TimeOnly From, TimeOnly To);

/// <summary>
/// Typed view of one class policy definition: the governed configuration the
/// Policy stage applies to every request of a (application, class) pair. The
/// version 1 vocabulary carries six fields: the eligible channels, the ordered
/// delivery plan, the default TTL, the dedupe window, the optional quiet-hours
/// window, and the optional consent purpose to consult.
/// </summary>
public sealed record ClassPolicyDefinition
{
    public required int SchemaVersion { get; init; }

    /// <summary>Channels a notification of this class may use. Anything else is filtered out.</summary>
    public required IReadOnlyList<Channel> ChannelsAllowed { get; init; }

    /// <summary>Attempt order and fallback timeouts.</summary>
    public required IReadOnlyList<DeliveryPlanStep> DeliveryPlan { get; init; }

    /// <summary>TTL applied when the producer sends none; never extends beyond the request.</summary>
    public required TimeSpan DefaultTtl { get; init; }

    /// <summary>Window inside which a repeated (templateKey, recipientId) is rejected as a duplicate.</summary>
    public required TimeSpan DedupeWindow { get; init; }

    /// <summary>Silence window that defers delivery; null means no window applies.</summary>
    public QuietHoursWindow? QuietHours { get; init; }

    /// <summary>Consent purpose to consult in the ledger; null means contractual/legal basis, no lookup.</summary>
    public string? ConsentPurpose { get; init; }

    /// <summary>
    /// Tolerant reader over a stored definition document: unknown fields never
    /// fail the read, so a definition written under a newer vocabulary still
    /// loads everywhere the version 1 fields are intact.
    /// </summary>
    public static Result<ClassPolicyDefinition> Read(string? definitionJson)
    {
        (ValidationReport report, ClassPolicyDefinition? definition) = ClassPolicyValidation.Parse(definitionJson);
        if (definition is not null)
        {
            return Result.Success(definition);
        }

        ValidationCheck failure = report.Checks
            .First(check => check.Status == ValidationCheckStatuses.Failed);
        return Result.ValidationError<ClassPolicyDefinition>(DomainError.Format(
            ErrorCodes.ClassPolicyValidationFailed,
            $"{failure.Name}: {failure.Message}"));
    }
}
