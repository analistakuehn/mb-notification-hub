using System.Text.RegularExpressions;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.TemplateManagement.Domain;

/// <summary>
/// Stable identifier a producer uses to reference a template, e.g. <c>auth.otp.login</c>.
/// Lowercase alphanumeric segments separated by <c>.</c>, <c>-</c> or <c>_</c>.
/// </summary>
public sealed partial record TemplateKey
{
    public const int MaxLength = 200;

    private TemplateKey(string value) => Value = value;

    public string Value { get; }

    public static Result<TemplateKey> Create(string? value)
    {
        string candidate = value?.Trim() ?? string.Empty;
        if (candidate.Length == 0 || candidate.Length > MaxLength || !Pattern().IsMatch(candidate))
        {
            return Result.ValidationError<TemplateKey>(DomainError.Format(
                ErrorCodes.InvalidRequest,
                $"A template key must be 1-{MaxLength} lowercase alphanumeric characters "
                + "in segments separated by '.', '-' or '_'."));
        }

        return Result.Success(new TemplateKey(candidate));
    }

    /// <summary>Rehydrates a key that already passed validation (persistence, canonical data).</summary>
    internal static TemplateKey Trusted(string value) => new(value);

    public override string ToString() => Value;

    [GeneratedRegex(@"^[a-z0-9]+(?:[._-][a-z0-9]+)*$")]
    private static partial Regex Pattern();
}
