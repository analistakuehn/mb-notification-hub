namespace NotificationHub.Api.Modules.TemplateManagement.Domain;

/// <summary>
/// Resolves the content locale a render should use: the exact requested locale,
/// then its base language (pt-BR falls back to pt), then the template's default
/// locale. Returns null when none of the three steps finds available content.
/// </summary>
public static class LocaleResolution
{
    public static Locale? Resolve(Locale requested, IReadOnlyList<Locale> available, Locale? defaultLocale)
    {
        ArgumentNullException.ThrowIfNull(requested);
        ArgumentNullException.ThrowIfNull(available);

        Locale? exact = Find(available, requested.Value);
        if (exact is not null)
        {
            return exact;
        }

        int separator = requested.Value.IndexOf('-', StringComparison.Ordinal);
        if (separator > 0)
        {
            Locale? baseLanguage = Find(available, requested.Value[..separator]);
            if (baseLanguage is not null)
            {
                return baseLanguage;
            }
        }

        return defaultLocale is null ? null : Find(available, defaultLocale.Value);
    }

    private static Locale? Find(IReadOnlyList<Locale> available, string value)
        => available.FirstOrDefault(locale => string.Equals(locale.Value, value, StringComparison.Ordinal));
}
