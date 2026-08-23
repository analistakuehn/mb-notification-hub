using System.Text.RegularExpressions;

namespace NotificationHub.Api.Modules.Dispatch.Infrastructure.Providers;

/// <summary>
/// Scrubs provider error text before it enters results, logs or attempt
/// records. Providers occasionally echo the destination back in validation
/// messages, and nothing downstream may carry personal data, so address-like
/// and long numeric tokens are masked and the text is capped.
/// </summary>
internal static partial class ProviderErrorSanitizer
{
    private const int MaxLength = 500;
    private const string Mask = "***";

    internal static string? Sanitize(string? providerMessage)
    {
        if (string.IsNullOrWhiteSpace(providerMessage))
        {
            return null;
        }

        var sanitized = EmailLikeToken().Replace(providerMessage, Mask);
        sanitized = LongDigitRun().Replace(sanitized, Mask);
        return sanitized.Length <= MaxLength ? sanitized : sanitized[..MaxLength];
    }

    [GeneratedRegex(@"[^\s@""',;:<>()\[\]]+@[^\s@""',;:<>()\[\]]+")]
    private static partial Regex EmailLikeToken();

    [GeneratedRegex(@"\d{6,}")]
    private static partial Regex LongDigitRun();
}
