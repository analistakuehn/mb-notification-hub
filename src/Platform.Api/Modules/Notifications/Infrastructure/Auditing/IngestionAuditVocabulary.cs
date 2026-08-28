using NotificationHub.Api.Modules.Notifications.Features.Ingress.RequestNotification;

namespace NotificationHub.Api.Modules.Notifications.Infrastructure.Auditing;

/// <summary>
/// Audit vocabulary of the ingestion, composed in this module's naming as the
/// audit contract expects from producing contexts. The action names follow
/// the platform-wide dot vocabulary; the actor is the producer identified by
/// its token (appid/oid), never a human session.
/// </summary>
internal static class IngestionAuditVocabulary
{
    internal const string ActorTypeProducer = "producer";

    internal const string EntityTypeNotification = "notification";

    internal const string NotificationAccepted = "notification.accepted";
    internal const string NotificationDuplicate = "notification.duplicate";
    internal const string NotificationRejectedAtIngress = "notification.rejected_at_ingress";

    /// <summary>Ingestion source of a synchronous producer call.</summary>
    internal const string SourceRest = "rest";

    /// <summary>Ingestion source of a producer event on the corporate bus.</summary>
    internal const string SourceKafka = "kafka";

    /// <summary>The recorded source of one ingestion origin.</summary>
    internal static string SourceOf(RequestNotification.IngestionSource source) => source switch
    {
        RequestNotification.IngestionSource.Rest => SourceRest,
        RequestNotification.IngestionSource.Kafka => SourceKafka,
        _ => throw new ArgumentOutOfRangeException(
            nameof(source), source, "Origem de ingestão desconhecida."),
    };
}
