namespace NotificationHub.Api.Modules.Notifications.Infrastructure.Privacy;

internal static partial class RenderedContentSweepLogger
{
    [LoggerMessage(EventId = 7160, Level = LogLevel.Information, Message = "Conteúdo completo descartado de {Settled} tentativas abandonadas de notificações expiradas antes de {Threshold}.")]
    internal static partial void RenderedContentSweepSettled(this ILogger logger, int settled, DateTimeOffset threshold);

    [LoggerMessage(EventId = 7161, Level = LogLevel.Information, Message = "Tentativa {AttemptId} da notificação {NotificationId}, parada em '{Status}', passou a guardar apenas a forma mascarada.")]
    internal static partial void AbandonedContentMasked(this ILogger logger, Guid attemptId, Guid notificationId, string status);

    [LoggerMessage(EventId = 7162, Level = LogLevel.Information, Message = "Varredura de conteúdo renderizado desabilitada por configuração.")]
    internal static partial void RenderedContentSweepDisabled(this ILogger logger);

    [LoggerMessage(EventId = 7163, Level = LogLevel.Information, Message = "Varredura de conteúdo renderizado iniciada com intervalo {Interval} e carência {Grace} sobre o vencimento.")]
    internal static partial void RenderedContentSweepStarted(this ILogger logger, TimeSpan interval, TimeSpan grace);

    [LoggerMessage(EventId = 7164, Level = LogLevel.Error, Message = "Falha em uma rodada da varredura de conteúdo renderizado; nova tentativa no próximo ciclo.")]
    internal static partial void RenderedContentSweepRoundFailed(this ILogger logger, Exception exception);
}
