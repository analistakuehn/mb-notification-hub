namespace NotificationHub.Api.Modules.Notifications.Infrastructure.Privacy;

internal static partial class RenderedContentBackfillLogger
{
    [LoggerMessage(EventId = 7170, Level = LogLevel.Information, Message = "Passe de backfill de conteúdo renderizado concluído: {Scanned} avaliadas, {Masked} substituídas, {AlreadyMasked} já mascaradas, {NeedsReview} para revisão.")]
    internal static partial void RenderedContentBackfillFinished(this ILogger logger, int scanned, int masked, int alreadyMasked, int needsReview);

    [LoggerMessage(EventId = 7171, Level = LogLevel.Information, Message = "Tentativa {AttemptId} da notificação {NotificationId}, em '{Status}', passou a guardar apenas a forma mascarada.")]
    internal static partial void RenderedContentSubstituted(this ILogger logger, Guid attemptId, Guid notificationId, string status);

    [LoggerMessage(EventId = 7172, Level = LogLevel.Warning, Message = "Tentativa {AttemptId} da notificação {NotificationId} não foi tocada e entra na lista de revisão pelo motivo '{Reason}'.")]
    internal static partial void RenderedContentNeedsReview(this ILogger logger, Guid attemptId, Guid notificationId, string reason);

    [LoggerMessage(EventId = 7173, Level = LogLevel.Information, Message = "Backfill de conteúdo renderizado desabilitado por configuração.")]
    internal static partial void RenderedContentBackfillDisabled(this ILogger logger);

    [LoggerMessage(EventId = 7174, Level = LogLevel.Error, Message = "Falha no passe de backfill de conteúdo renderizado; nenhuma linha adicional foi tocada.")]
    internal static partial void RenderedContentBackfillFailed(this ILogger logger, Exception exception);
}
