namespace NotificationHub.Api.Modules.Notifications.Domain;

/// <summary>
/// Canonical notification classes of the ingestion vocabulary and the app role
/// that authorizes a producer to request each one. The class arrives as data
/// in the request body, so the role check runs against the resource, not the
/// route: a billing service never requests <c>critical</c>.
/// </summary>
public static class NotificationClasses
{
    public const string Critical = "critical";
    public const string Transactional = "transactional";
    public const string Operational = "operational";

    public static IReadOnlyList<string> CanonicalValues { get; } =
        [Critical, Transactional, Operational];

    public static bool IsCanonical(string? value)
        => value is Critical or Transactional or Operational;

    /// <summary>App role that authorizes requesting the given canonical class.</summary>
    public static string RequiredRole(string canonicalClass) => canonicalClass switch
    {
        Critical => "Notifications.Send.Critical",
        Transactional => "Notifications.Send.Transactional",
        Operational => "Notifications.Send.Operational",
        _ => throw new ArgumentOutOfRangeException(
            nameof(canonicalClass), canonicalClass, "Classe de notificação desconhecida."),
    };
}
