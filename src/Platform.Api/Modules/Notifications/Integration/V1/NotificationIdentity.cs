using NotificationHub.Api.Modules.Notifications.Domain;

namespace NotificationHub.Api.Modules.Notifications.Integration.V1;

/// <summary>
/// The public form of a notification identity, published so a consumer can tell
/// a malformed identity from an unknown one without reimplementing the encoding.
/// A consumer that could not parse would have to answer both cases the same way,
/// and an answer that never distinguishes a bad shape from a missing row loses
/// the diagnosis a caller needs.
/// </summary>
public static class NotificationIdentity
{
    /// <summary>Formats the stored identity as the public form the ingestion returns.</summary>
    public static string Format(Guid id) => NotificationId.Format(id);

    /// <summary>Parses the public form back into the stored identity.</summary>
    public static bool TryParse(string? value, out Guid id) => NotificationId.TryParse(value, out id);
}
