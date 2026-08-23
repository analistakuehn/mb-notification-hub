namespace NotificationHub.Api.Modules.ContactConsent.Domain;

/// <summary>
/// Delivery preferences of one recipient: the IANA timezone that feeds quiet
/// hours and the preferred locale. The row also anchors every contact point
/// and device token of the recipient. An absent timezone means the platform
/// default; the read contract applies it, so the column stores exactly what
/// was declared.
/// </summary>
public sealed class RecipientProfile
{
    /// <summary>Timezone assumed for a recipient that never declared one.</summary>
    public const string DefaultTimezone = "America/Sao_Paulo";

    private RecipientProfile()
    {
        RecipientId = null!;
    }

    public string RecipientId { get; private set; }

    /// <summary>IANA timezone id; null means <see cref="DefaultTimezone"/> applies.</summary>
    public string? Timezone { get; private set; }

    public string? Locale { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static RecipientProfile Create(
        string recipientId,
        string? timezone,
        string? locale,
        DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recipientId);
        return new RecipientProfile
        {
            RecipientId = recipientId,
            Timezone = timezone,
            Locale = locale,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    /// <summary>
    /// Applies the declared preferences and reports whether anything changed.
    /// A null argument leaves the current value untouched: the profile fields
    /// ride on the contact-points declaration, and an omitted field is not a
    /// request to clear it.
    /// </summary>
    public bool ApplyPreferences(string? timezone, string? locale, DateTimeOffset now)
    {
        var changed = false;
        if (timezone is not null && !string.Equals(Timezone, timezone, StringComparison.Ordinal))
        {
            Timezone = timezone;
            changed = true;
        }

        if (locale is not null && !string.Equals(Locale, locale, StringComparison.Ordinal))
        {
            Locale = locale;
            changed = true;
        }

        if (changed)
        {
            UpdatedAt = now;
        }

        return changed;
    }

    /// <summary>Effective timezone with the platform default applied.</summary>
    public string EffectiveTimezone => Timezone ?? DefaultTimezone;
}
