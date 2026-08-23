namespace NotificationHub.Api.Modules.Notifications.Infrastructure.Http;

/// <summary>
/// Published numbers of the query surface. They are contract, not
/// configuration: a caller pages against them, a client library encodes them,
/// and moving one silently would change what an already-written integration
/// receives. Changing any of them is a contract change with its own review.
/// </summary>
internal static class NotificationQueryContract
{
    /// <summary>Page size applied when the caller states none.</summary>
    internal const int DefaultPageSize = 50;

    /// <summary>Largest page a caller may ask for.</summary>
    internal const int MaxPageSize = 200;

    /// <summary>
    /// Span applied backwards from the upper bound when the caller states no
    /// lower bound. A history read without a window would scan every monthly
    /// partition, because the indexes that serve it are local per partition.
    /// </summary>
    internal static readonly TimeSpan DefaultWindow = TimeSpan.FromDays(90);

    /// <summary>Largest window a caller may ask for in one read.</summary>
    internal static readonly TimeSpan MaxWindow = TimeSpan.FromDays(180);

    /// <summary>
    /// Notice carried by the OpenAPI description of every query route: the
    /// query reads a replica, so a read issued right after the acceptance may
    /// not see the notification yet.
    /// </summary>
    internal const string ReplicationLagNotice =
        "A consulta lê a réplica de leitura. Logo após o 202 da ingestão, a notificação pode ainda não ter "
        + "replicado, e uma leitura imediata pode devolver 404 ou um estado anterior ao mais recente.";
}
