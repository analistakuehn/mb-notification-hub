using System.Text.RegularExpressions;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.TemplateManagement.Domain;

/// <summary>
/// Stable identifier of a layout, e.g. <c>email.base</c>. Lowercase
/// alphanumeric segments separated by <c>.</c>, <c>-</c> or <c>_</c>.
/// </summary>
public sealed partial record LayoutKey
{
    public const int MaxLength = 200;

    private LayoutKey(string value) => Value = value;

    public string Value { get; }

    public static Result<LayoutKey> Create(string? value)
    {
        var candidate = value?.Trim() ?? string.Empty;
        if (candidate.Length == 0 || candidate.Length > MaxLength || !Pattern().IsMatch(candidate))
        {
            return Result.ValidationError<LayoutKey>(DomainError.Format(
                ErrorCodes.InvalidRequest,
                $"A layout key must be 1-{MaxLength} lowercase alphanumeric characters "
                + "in segments separated by '.', '-' or '_'."));
        }

        return Result.Success(new LayoutKey(candidate));
    }

    /// <summary>Rehydrates a key that already passed validation (persistence, canonical data).</summary>
    internal static LayoutKey Trusted(string value) => new(value);

    public override string ToString() => Value;

    [GeneratedRegex(@"^[a-z0-9]+(?:[._-][a-z0-9]+)*$")]
    private static partial Regex Pattern();
}
