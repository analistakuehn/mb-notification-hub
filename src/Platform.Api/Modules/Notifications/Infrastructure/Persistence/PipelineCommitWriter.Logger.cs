namespace NotificationHub.Api.Modules.Notifications.Infrastructure.Persistence;

internal static partial class PipelineCommitWriterLogger
{
    [LoggerMessage(EventId = 7110, Level = LogLevel.Warning, Message = "Notificação {NotificationId} da classe {Class} adiada até {ReleaseAt} pela janela de silêncio; o liberador de deferred ainda não existe nesta fase.")]
    internal static partial void NotificationDeferred(this ILogger logger, Guid notificationId, string @class, DateTimeOffset releaseAt);
}
