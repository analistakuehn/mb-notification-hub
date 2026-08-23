namespace NotificationHub.Api.Modules.ContactConsent.Domain;

/// <summary>Canonical platforms of a push device registration.</summary>
public static class DevicePlatforms
{
    public const string Ios = "ios";
    public const string Android = "android";
    public const string Web = "web";

    public static IReadOnlyList<string> CanonicalValues { get; } = [Ios, Android, Web];

    public static bool IsCanonical(string? value)
        => value is Ios or Android or Web;
}

/// <summary>
/// One push token registered by the recipient's app on one device. A
/// re-registration of the same token refreshes the last-seen instant and the
/// app version, which is what orders the dispatch fan-out. Invalidation is
/// stamped by the provider feedback path, through the published lifecycle
/// contract, when the provider declares the token dead.
/// </summary>
public sealed class DeviceToken
{
    private DeviceToken()
    {
        RecipientId = null!;
        Token = null!;
        Platform = null!;
    }

    public Guid Id { get; private set; }

    public string RecipientId { get; private set; }

    public string Token { get; private set; }

    public string Platform { get; private set; }

    public string? AppVersion { get; private set; }

    public DateTimeOffset RegisteredAt { get; private set; }

    public DateTimeOffset LastSeenAt { get; private set; }

    /// <summary>Stamped by provider feedback; null while the token is active.</summary>
    public DateTimeOffset? InvalidatedAt { get; private set; }

    public bool IsActive => InvalidatedAt is null;

    /// <summary>
    /// Stamps the invalidation instant on provider feedback. Idempotent: an
    /// already invalidated token keeps its first instant, so a repeated
    /// report never rewrites history.
    /// </summary>
    public void Invalidate(DateTimeOffset now)
    {
        InvalidatedAt ??= now;
    }

    public static DeviceToken Register(
        string recipientId,
        string token,
        string platform,
        string? appVersion,
        DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recipientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        if (!DevicePlatforms.IsCanonical(platform))
        {
            throw new ArgumentException($"Plataforma de dispositivo desconhecida: '{platform}'.", nameof(platform));
        }

        return new DeviceToken
        {
            Id = Guid.CreateVersion7(),
            RecipientId = recipientId,
            Token = token,
            Platform = platform,
            AppVersion = appVersion,
            RegisteredAt = now,
            LastSeenAt = now,
        };
    }

    /// <summary>Refreshes the last-seen instant and app version on a re-registration.</summary>
    public void Touch(string? appVersion, DateTimeOffset now)
    {
        LastSeenAt = now;
        if (appVersion is not null)
        {
            AppVersion = appVersion;
        }
    }
}
