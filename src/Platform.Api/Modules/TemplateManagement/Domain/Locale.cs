using System.Text.RegularExpressions;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.TemplateManagement.Domain;

/// <summary>
/// BCP 47 language tag restricted to language or language-region form
/// (<c>pt</c>, <c>pt-BR</c>). Normalized to lowercase language and uppercase region.
/// </summary>
public sealed partial record Locale
{
    private Locale(string value) => Value = value;

    public string Value { get; }

    public static Result<Locale> Create(string? value)
    {
        var candidate = value?.Trim() ?? string.Empty;
        Match match = Pattern().Match(candidate);
        if (!match.Success)
        {
            return Result.ValidationError<Locale>(DomainError.Format(
                ErrorCodes.InvalidRequest,
                $"Locale '{value}' is not supported. Use a language tag such as 'pt' or 'pt-BR'."));
        }

        var language = match.Groups["language"].Value.ToLowerInvariant();
        var region = match.Groups["region"].Value;
        var normalized = region.Length == 0 ? language : $"{language}-{region.ToUpperInvariant()}";
        return Result.Success(new Locale(normalized));
    }

    /// <summary>Rehydrates a locale that already passed validation (persistence, canonical data).</summary>
    internal static Locale Trusted(string value) => new(value);

    public override string ToString() => Value;

    [GeneratedRegex(@"^(?<language>[A-Za-z]{2})(?:-(?<region>[A-Za-z]{2}))?$")]
    private static partial Regex Pattern();
}
